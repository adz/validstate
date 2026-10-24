namespace MyApp.UI

open System
open System.Diagnostics
open System.Diagnostics.CodeAnalysis
open System.Reflection
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml
open MyApp

[<RequireQualifiedAccess>]
type LifecycleState =
    | Starting
    | Running
    | Stopping
    | Stopped
    | Failed

[<RequireQualifiedAccess>]
type private ShutdownKind =
    | Application
    | OperatingSystem

/// A startup operation retains ownership of a runtime until the UI accepts it. Cancelling the operation waits for
/// provisioning and stops a runtime that arrives late, without depending on the UI dispatcher.
type StartupOperation<'runtime>
    (
        completion: Async<Result<'runtime, exn>>,
        cancel: unit -> Async<Result<unit, exn>>
    ) =
    member _.Completion = completion
    member _.Cancel() = cancel ()

[<RequireQualifiedAccess>]
module StartupOperation =
    let create (start: Async<'runtime>) (stop: 'runtime -> Async<unit>) =
        let gate = obj ()
        let completion = TaskCompletionSource<Result<'runtime, exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let cancellation = TaskCompletionSource<Result<unit, exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable result: Result<'runtime, exn> option = None
        let mutable cancellationRequested = false
        let mutable cleanupStarted = false

        let startCleanup running =
            let stopping = stop running |> Async.StartAsTask

            stopping.ContinueWith(
                (fun (completed: Task<unit>) ->
                    try
                        completed.GetAwaiter().GetResult()
                        cancellation.TrySetResult(Ok ()) |> ignore
                    with error ->
                        cancellation.TrySetResult(Error error) |> ignore),
                TaskScheduler.Default)
            |> ignore

        let observe completed =
            let cleanup =
                lock gate (fun () ->
                    result <- Some completed
                    completion.TrySetResult(completed) |> ignore

                    if cancellationRequested && not cleanupStarted then
                        cleanupStarted <- true
                        Some completed
                    else
                        None)

            match cleanup with
            | Some(Ok running) -> startCleanup running
            | Some(Error error) -> cancellation.TrySetResult(Error error) |> ignore
            | None -> ()

        let starting = start |> Async.StartAsTask

        starting.ContinueWith(
            (fun (completed: Task<'runtime>) ->
                try observe (Ok(completed.GetAwaiter().GetResult()))
                with error -> observe (Error error)),
            TaskScheduler.Default)
        |> ignore

        let cancel () =
            let cleanup =
                lock gate (fun () ->
                    cancellationRequested <- true

                    if not cleanupStarted then
                        match result with
                        | Some completed ->
                            cleanupStarted <- true
                            Some completed
                        | None -> None
                    else
                        None)

            match cleanup with
            | Some(Ok running) -> startCleanup running
            | Some(Error error) -> cancellation.TrySetResult(Error error) |> ignore
            | None -> ()

            cancellation.Task |> Async.AwaitTask

        StartupOperation(completion.Task |> Async.AwaitTask, cancel)

module private ShutdownRequest =
    // Avalonia 12.1 keeps this startup-phase signal internal. A running window uses its public CloseReason instead.
    [<DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties ||| DynamicallyAccessedMemberTypes.NonPublicProperties, typeof<ShutdownRequestedEventArgs>)>]
    let classify (args: ShutdownRequestedEventArgs) =
        try
            let property =
                typeof<ShutdownRequestedEventArgs>.GetProperty(
                    "IsOSShutdown",
                    BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)

            if isNull property then
                Error(MissingMemberException(typeof<ShutdownRequestedEventArgs>.FullName, "IsOSShutdown") :> exn)
            else
                match property.GetValue(args) with
                | :? bool as isOsShutdown -> Ok isOsShutdown
                | value ->
                    let actual = if isNull value then "null" else value.GetType().FullName
                    Error(InvalidCastException($"ShutdownRequestedEventArgs.IsOSShutdown returned {actual}, not Boolean.") :> exn)
        with error ->
            Error error

/// Coordinates asynchronous runtime startup and shutdown around Avalonia's synchronous lifetime events.
type DesktopLifecycle<'runtime>
    (
        desktop: IClassicDesktopStyleApplicationLifetime,
        beginStartup: unit -> StartupOperation<'runtime>,
        stopRuntime: 'runtime -> Async<unit>,
        startUi: 'runtime -> Window * IDisposable,
        post: (unit -> unit) -> unit,
        reportError: exn -> unit,
        exit: int -> unit,
        delay: TimeSpan -> Async<unit>,
        stopTimeout: TimeSpan,
        classifyShutdown: ShutdownRequestedEventArgs -> Result<bool, exn>
    ) =

    let mutable state = LifecycleState.Starting
    let mutable startup: StartupOperation<'runtime> option = None
    let mutable runtime: 'runtime option = None
    let mutable window: Window option = None
    let mutable connection: IDisposable option = None
    let mutable shutdownKind: ShutdownKind option = None
    let mutable stopStarted = false
    let mutable classificationWarningReported = false
    let mutable shutdownError: exn option = None

    let report error =
        try reportError error
        with _ -> ()

    let classify args =
        try
            match classifyShutdown args with
            | Ok isOsShutdown -> isOsShutdown
            | Error error ->
                if not classificationWarningReported then
                    classificationWarningReported <- true
                    report error
                false
        with error ->
            if not classificationWarningReported then
                classificationWarningReported <- true
                report error
            false

    let finish exitCode nextState =
        state <- nextState
        exit exitCode

    let completeStop result =
        match result, shutdownError with
        | Ok (), None ->
            state <- LifecycleState.Stopped
            if shutdownKind <> Some ShutdownKind.OperatingSystem then exit 0
        | Error error, _ ->
            report error
            finish 1 LifecycleState.Failed
        | Ok (), Some error ->
            report error
            finish 1 LifecycleState.Failed

    // The operation is started independently before racing its completion against the injected delay. Timing out
    // therefore never cancels cleanup; it only bounds how long the host keeps its dispatcher alive.
    let runBounded description operation =
        let completed = TaskCompletionSource<Result<unit, exn>>(TaskCreationOptions.RunContinuationsAsynchronously)

        let running = operation |> Async.StartAsTask

        running.ContinueWith(
            (fun (finished: Task<Result<unit, exn>>) ->
                try completed.TrySetResult(finished.GetAwaiter().GetResult()) |> ignore
                with error -> completed.TrySetResult(Error error) |> ignore),
            TaskScheduler.Default)
        |> ignore

        async {
            let! outcome =
                Async.Choice
                    [ async {
                          let! result = completed.Task |> Async.AwaitTask
                          return Some result
                      }
                      async {
                          do! delay stopTimeout
                          return Some(Error(TimeoutException(description + " exceeded its " + stopTimeout.TotalSeconds.ToString(Globalization.CultureInfo.InvariantCulture) + " second deadline.") :> exn))
                      } ]

            return outcome.Value
        }

    let startBoundedStop operation =
        Async.StartWithContinuations(
            runBounded "Runtime shutdown" operation,
            (fun result -> post (fun () -> completeStop result)),
            (fun error -> post (fun () -> completeStop (Error error))),
            (fun cancelled -> post (fun () -> completeStop (Error cancelled))))

    let startRuntimeStop () =
        match runtime with
        | Some running when not stopStarted ->
            stopStarted <- true
            startBoundedStop (async {
                do! stopRuntime running
                return Ok ()
            })
        | _ -> ()

    let beginStop kind =
        shutdownKind <-
            match shutdownKind, kind with
            | _, ShutdownKind.OperatingSystem -> Some ShutdownKind.OperatingSystem
            | Some existing, _ -> Some existing
            | None, _ -> Some kind

        if state <> LifecycleState.Stopping then state <- LifecycleState.Stopping

        match connection with
        | Some active ->
            connection <- None
            try active.Dispose()
            with error -> shutdownError <- Some error
        | None -> ()

        match runtime, startup with
        | Some _, _ -> startRuntimeStop ()
        | None, Some pending when not stopStarted ->
            stopStarted <- true
            startBoundedStop (pending.Cancel())
        | _ -> ()

    let windowClosing (args: WindowClosingEventArgs) =
        if args.CloseReason = WindowCloseReason.OSShutdown then
            beginStop ShutdownKind.OperatingSystem
        else
            match state with
            | LifecycleState.Starting
            | LifecycleState.Running ->
                args.Cancel <- true
                beginStop ShutdownKind.Application
            | LifecycleState.Stopping -> args.Cancel <- true
            | LifecycleState.Stopped
            | LifecycleState.Failed -> ()

    let startupSucceeded running =
        runtime <- Some running

        match state with
        | LifecycleState.Starting ->
            try
                let createdWindow, active = startUi running
                window <- Some createdWindow
                connection <- Some active
                createdWindow.Closing.Add windowClosing
                desktop.MainWindow <- createdWindow
                desktop.ShutdownMode <- ShutdownMode.OnLastWindowClose
                state <- LifecycleState.Running
                createdWindow.Show()
            with error ->
                shutdownError <- Some error
                beginStop ShutdownKind.Application
        | LifecycleState.Stopping
        | LifecycleState.Stopped
        | LifecycleState.Failed
        | LifecycleState.Running ->
            // StartupOperation owns cleanup after cancellation, including after the dispatcher exits.
            ()

    let startupFailed error =
        match state with
        | LifecycleState.Starting ->
            report error
            finish 1 LifecycleState.Failed
        | LifecycleState.Stopping
        | LifecycleState.Stopped
        | LifecycleState.Failed
        | LifecycleState.Running -> ()

    let shutdownRequested (_: obj) (args: ShutdownRequestedEventArgs) =
        try
            match state, window with
            | LifecycleState.Starting, _
            | LifecycleState.Stopping, None ->
                if classify args then beginStop ShutdownKind.OperatingSystem
                else
                    args.Cancel <- true
                    beginStop ShutdownKind.Application
            | LifecycleState.Running, Some _
            | LifecycleState.Stopping, Some _ ->
                // Avalonia now proceeds to Window.Closing, whose public CloseReason distinguishes OS shutdown.
                ()
            | LifecycleState.Stopped, _
            | LifecycleState.Failed, _
            | LifecycleState.Running, None -> ()
        with error ->
            // A host callback must never escape into Avalonia's synchronous shutdown dispatch.
            args.Cancel <- true
            report error
            beginStop ShutdownKind.Application

    member _.State = state
    member _.Runtime = runtime

    member _.Start() =
        desktop.ShutdownMode <- ShutdownMode.OnExplicitShutdown
        desktop.ShutdownRequested.AddHandler(EventHandler<ShutdownRequestedEventArgs>(shutdownRequested))

        let pending = beginStartup ()
        startup <- Some pending

        Async.StartWithContinuations(
            pending.Completion,
            (fun result ->
                post (fun () ->
                    match result with
                    | Ok running -> startupSucceeded running
                    | Error error -> startupFailed error)),
            (fun error -> post (fun () -> startupFailed error)),
            (fun cancelled -> post (fun () -> startupFailed cancelled)))

[<RequireQualifiedAccess>]
module DesktopHost =
    let start (desktop: IClassicDesktopStyleApplicationLifetime) =
        let reportError (error: exn) = Trace.TraceError(error.ToString())
        let stop (runtime: Runtime) = runtime.Stop()

        let lifecycle =
            DesktopLifecycle<Runtime>(
                desktop,
                (fun () -> StartupOperation.create (Runtime.live Shell.postToDispatcher) stop),
                stop,
                (fun runtime ->
                    let env = runtime.Env
                    let createdWindow, connection =
                        Shell.create runtime (Shell.folder env (if isNull desktop.Args then [||] else desktop.Args))
                    createdWindow, (connection :> IDisposable)),
                Shell.postToDispatcher,
                reportError,
                (fun exitCode -> desktop.Shutdown exitCode),
                (fun timeout -> Async.Sleep(max 1 (int timeout.TotalMilliseconds))),
                TimeSpan.FromSeconds 5.0,
                ShutdownRequest.classify)

        lifecycle.Start()
        lifecycle

type App() =
    inherit Application()

    override this.Initialize() = AvaloniaXamlLoader.Load this

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> DesktopHost.start desktop |> ignore
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
