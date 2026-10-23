namespace MyApp.UI

open System
open System.Diagnostics
open System.Diagnostics.CodeAnalysis
open System.Reflection
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

module private ShutdownRequest =
    // Avalonia 12.1 exposes this information to its lifetime implementation but not yet through its public API.
    // Root the internal property so this narrow compatibility check remains available under NativeAOT trimming.
    [<DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicProperties, typeof<ShutdownRequestedEventArgs>)>]
    let isOperatingSystem (args: ShutdownRequestedEventArgs) =
        let property =
            typeof<ShutdownRequestedEventArgs>.GetProperty(
                "IsOSShutdown",
                BindingFlags.Instance ||| BindingFlags.NonPublic)

        property.GetValue(args) :?> bool

/// Coordinates asynchronous runtime startup and shutdown around Avalonia's synchronous lifetime events.
/// The generic runtime type keeps the state machine independently testable while the application uses `Runtime`.
type DesktopLifecycle<'runtime>
    (
        desktop: IClassicDesktopStyleApplicationLifetime,
        startRuntime: unit -> Async<'runtime>,
        stopRuntime: 'runtime -> Async<unit>,
        startUi: 'runtime -> Window * IDisposable,
        post: (unit -> unit) -> unit,
        reportError: exn -> unit,
        exit: int -> unit,
        stopTimeout: TimeSpan
    ) =

    let mutable state = LifecycleState.Starting
    let mutable runtime: 'runtime option = None
    let mutable connection: IDisposable option = None
    let mutable shutdownKind: ShutdownKind option = None
    let mutable stopStarted = false
    let mutable startupShutdownTimerStarted = false
    let mutable terminalFailure = false
    let mutable shutdownError: exn option = None

    let report error =
        try reportError error
        with _ -> ()

    let finish exitCode nextState =
        state <- nextState
        if nextState = LifecycleState.Failed then terminalFailure <- true
        exit exitCode

    let completeStop result =
        if terminalFailure then
            state <- LifecycleState.Failed
            match result with
            | Error error -> report error
            | Ok () -> ()
        else
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

    let startStop () =
        match runtime with
        | Some running when not stopStarted ->
            stopStarted <- true

            let boundedStop =
                async {
                    let milliseconds = max 1 (int stopTimeout.TotalMilliseconds)
                    let! stopping = Async.StartChild(stopRuntime running, milliseconds)
                    do! stopping
                }

            Async.StartWithContinuations(
                boundedStop,
                (fun () -> post (fun () -> completeStop (Ok ()))),
                (fun error -> post (fun () -> completeStop (Error error))),
                (fun cancelled -> post (fun () -> completeStop (Error cancelled))))
        | _ -> ()

    let startStartupShutdownTimer () =
        if not startupShutdownTimerStarted then
            startupShutdownTimerStarted <- true
            let milliseconds = max 1 (int stopTimeout.TotalMilliseconds)

            Async.StartWithContinuations(
                async { do! Async.Sleep milliseconds },
                (fun () ->
                    post (fun () ->
                        if state = LifecycleState.Stopping && Option.isNone runtime then
                            let error = TimeoutException("Runtime startup did not finish before shutdown timed out.")
                            report error
                            finish 1 LifecycleState.Failed)),
                (fun error -> post (fun () -> report error; finish 1 LifecycleState.Failed)),
                (fun cancelled -> post (fun () -> report cancelled; finish 1 LifecycleState.Failed)))

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

        match runtime, kind with
        | None, ShutdownKind.Application -> startStartupShutdownTimer ()
        | _ -> startStop ()

    let startupSucceeded running =
        runtime <- Some running

        match state with
        | LifecycleState.Starting ->
            try
                let window, active = startUi running
                connection <- Some active
                desktop.MainWindow <- window
                desktop.ShutdownMode <- ShutdownMode.OnLastWindowClose
                state <- LifecycleState.Running
                window.Show()
            with error ->
                shutdownError <- Some error
                beginStop ShutdownKind.Application
        | LifecycleState.Stopping -> startStop ()
        | LifecycleState.Running -> ()
        | LifecycleState.Stopped
        | LifecycleState.Failed ->
            // A late completion must not leave a newly-provisioned runtime alive.
            shutdownKind <- Some ShutdownKind.Application
            state <- LifecycleState.Stopping
            startStop ()

    let startupFailed error =
        report error
        finish 1 LifecycleState.Failed

    let shutdownRequested (_: obj) (args: ShutdownRequestedEventArgs) =
        let osShutdown = ShutdownRequest.isOperatingSystem args

        match state with
        | LifecycleState.Starting ->
            if not osShutdown then args.Cancel <- true
            beginStop (if osShutdown then ShutdownKind.OperatingSystem else ShutdownKind.Application)
        | LifecycleState.Running ->
            if not osShutdown then args.Cancel <- true
            beginStop (if osShutdown then ShutdownKind.OperatingSystem else ShutdownKind.Application)
        | LifecycleState.Stopping ->
            // Never block an OS session end. Other repeated requests wait for the bounded Stop to finish.
            if osShutdown then beginStop ShutdownKind.OperatingSystem
            else args.Cancel <- true
        | LifecycleState.Stopped
        | LifecycleState.Failed -> ()

    member _.State = state

    member _.Start() =
        desktop.ShutdownMode <- ShutdownMode.OnExplicitShutdown
        desktop.ShutdownRequested.AddHandler(EventHandler<ShutdownRequestedEventArgs>(shutdownRequested))

        Async.StartWithContinuations(
            startRuntime (),
            (fun running -> post (fun () -> startupSucceeded running)),
            (fun error -> post (fun () -> startupFailed error)),
            (fun cancelled -> post (fun () -> startupFailed cancelled)))

type App() =
    inherit Application()

    override this.Initialize() = AvaloniaXamlLoader.Load this

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let reportError (error: exn) = Trace.TraceError(error.ToString())

            DesktopLifecycle<Runtime>(
                desktop,
                (fun () -> Runtime.live Shell.postToDispatcher),
                _.Stop(),
                (fun runtime ->
                    let env = runtime.Env
                    let window, connection =
                        Shell.create runtime (Shell.folder env (if isNull desktop.Args then [||] else desktop.Args))
                    window, (connection :> IDisposable)),
                Shell.postToDispatcher,
                reportError,
                (fun exitCode -> desktop.Shutdown exitCode),
                TimeSpan.FromSeconds 5.0)
                .Start()
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
