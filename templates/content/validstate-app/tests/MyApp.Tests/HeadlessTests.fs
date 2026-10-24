module MyApp.Tests.HeadlessTests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Headless
open Avalonia.Threading
open Axial.PlatformService
open Xunit
open MyApp
open MyApp.UI
open MyApp.Tests.Support

/// Pumps the UI until a named condition holds. The limit is an upper bound, not a delay, and a timeout
/// names what it was waiting for.
let private pumpUntil (description: string) (condition: unit -> bool) =
    let clock = Stopwatch.StartNew()

    while not (condition ()) && clock.Elapsed < TimeSpan.FromSeconds 60.0 do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep 10

    Dispatcher.UIThread.RunJobs()
    if not (condition ()) then failwith $"Timed out waiting for: {description}"

let private disposable (dispose: unit -> unit) =
    { new IDisposable with member _.Dispose() = dispose () }

let private neverDelay (_: TimeSpan) =
    let pending = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    pending.Task |> Async.AwaitTask

let private closeForOsShutdown (window: Window) =
    let method =
        typeof<Window>.GetMethod(
            "CloseCore",
            BindingFlags.Instance ||| BindingFlags.NonPublic)

    method.Invoke(window, [| WindowCloseReason.OSShutdown; false; false |]) |> ignore

let private raiseLifetimeShutdown (lifetime: ClassicDesktopStyleApplicationLifetime) (args: ShutdownRequestedEventArgs) =
    let eventField =
        typeof<ClassicDesktopStyleApplicationLifetime>.GetField(
            "ShutdownRequested",
            BindingFlags.Instance ||| BindingFlags.NonPublic)

    let handlers = eventField.GetValue(lifetime) :?> EventHandler<ShutdownRequestedEventArgs>
    handlers.Invoke(lifetime, args)

type private LifecycleHarness =
    { Lifetime: ClassicDesktopStyleApplicationLifetime
      Lifecycle: DesktopLifecycle<int>
      StopCalls: unit -> int
      UiStarts: unit -> int
      Disposed: unit -> int
      ExitCode: unit -> int option
      Errors: ResizeArray<exn>
      Window: unit -> Window option }

let private lifecycleWith
    (stopTimeout: TimeSpan)
    (delay: TimeSpan -> Async<unit>)
    (classify: ShutdownRequestedEventArgs -> Result<bool, exn>)
    (post: (unit -> unit) -> unit)
    (onExit: int -> unit)
    (start: unit -> Async<int>)
    (stop: int -> Async<unit>) =
    let lifetime = new ClassicDesktopStyleApplicationLifetime()
    let mutable stopCalls = 0
    let mutable uiStarts = 0
    let mutable disposed = 0
    let mutable exitCode: int option = None
    let errors = ResizeArray<exn>()
    let mutable window: Window option = None
    let trackedStop runtime =
        stopCalls <- stopCalls + 1
        stop runtime

    let lifecycle =
        DesktopLifecycle<int>(
            lifetime,
            (fun () -> StartupOperation.create (start ()) trackedStop),
            trackedStop,
            (fun _ ->
                uiStarts <- uiStarts + 1
                let created = Window()
                window <- Some created
                created, disposable (fun () -> disposed <- disposed + 1)),
            post,
            errors.Add,
            (fun code -> exitCode <- Some code; onExit code),
            delay,
            stopTimeout,
            classify)

    lifecycle.Start()

    { Lifetime = lifetime
      Lifecycle = lifecycle
      StopCalls = (fun () -> stopCalls)
      UiStarts = (fun () -> uiStarts)
      Disposed = (fun () -> disposed)
      ExitCode = (fun () -> exitCode)
      Errors = errors
      Window = (fun () -> window) }

let private lifecycle start stop =
    lifecycleWith
        (TimeSpan.FromSeconds 1.0)
        neverDelay
        (fun _ -> Ok false)
        Shell.postToDispatcher
        ignore
        start
        stop

let private requestWindowClose harness =
    harness.Window() |> Option.iter _.Close()

let private verifyFirstAndRepeatedShutdown () =
    let stopped = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let harness = lifecycle (fun () -> async.Return 1) (fun _ -> stopped.Task |> Async.AwaitTask)

    pumpUntil "the lifecycle to reach Running" (fun () -> harness.Lifecycle.State = LifecycleState.Running)
    Assert.Equal(1, harness.UiStarts())
    requestWindowClose harness
    Assert.Equal(LifecycleState.Stopping, harness.Lifecycle.State)
    Assert.Equal(1, harness.StopCalls())
    requestWindowClose harness
    Assert.Equal(1, harness.StopCalls())
    Assert.Equal(None, harness.ExitCode())

    stopped.SetResult()
    pumpUntil "the lifecycle to complete shutdown" (fun () -> harness.Lifecycle.State = LifecycleState.Stopped)
    Assert.Equal(Some 0, harness.ExitCode())
    Assert.Equal(1, harness.Disposed())
    Assert.Empty(harness.Errors)

let private verifyOsShutdownUsesWindowReason () =
    let stopped = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let harness = lifecycle (fun () -> async.Return 1) (fun _ -> stopped.Task |> Async.AwaitTask)

    pumpUntil "the OS-shutdown lifecycle to reach Running" (fun () -> harness.Lifecycle.State = LifecycleState.Running)
    closeForOsShutdown (harness.Window().Value)
    Assert.Equal(LifecycleState.Stopping, harness.Lifecycle.State)
    Assert.Equal(1, harness.StopCalls())
    Assert.Equal(1, harness.Disposed())

    stopped.SetResult()
    pumpUntil "the OS-shutdown Stop to complete" (fun () -> harness.Lifecycle.State = LifecycleState.Stopped)
    Assert.Equal(None, harness.ExitCode())
    Assert.Empty(harness.Errors)

let private verifyStopFailure () =
    let failure = InvalidOperationException("stop failed")
    let harness = lifecycle (fun () -> async.Return 1) (fun _ -> async { return raise failure })

    pumpUntil "the failing lifecycle to reach Running" (fun () -> harness.Lifecycle.State = LifecycleState.Running)
    requestWindowClose harness
    pumpUntil "the failing lifecycle to terminate" (fun () -> harness.Lifecycle.State = LifecycleState.Failed)
    Assert.Equal(Some 1, harness.ExitCode())
    Assert.True(harness.Errors |> Seq.exists (fun error -> Object.ReferenceEquals(error, failure)))

let private verifyStopTimeout () =
    let stopped = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let deadline = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let harness =
        lifecycleWith
            (TimeSpan.FromSeconds 1.0)
            (fun _ -> deadline.Task |> Async.AwaitTask)
            (fun _ -> Ok false)
            Shell.postToDispatcher
            ignore
            (fun () -> async.Return 1)
            (fun _ -> stopped.Task |> Async.AwaitTask)

    pumpUntil "the timeout lifecycle to reach Running" (fun () -> harness.Lifecycle.State = LifecycleState.Running)
    requestWindowClose harness
    deadline.SetResult()
    pumpUntil "the Stop deadline to expire" (fun () -> harness.Lifecycle.State = LifecycleState.Failed)
    Assert.Equal(Some 1, harness.ExitCode())
    Assert.True(harness.Errors |> Seq.exists (fun error -> error :? TimeoutException))

let private verifyStartupFailure () =
    let failure = InvalidOperationException("startup failed")
    let harness = lifecycle (fun () -> async { return raise failure }) (fun _ -> async.Return ())

    pumpUntil "the startup failure to terminate" (fun () -> harness.Lifecycle.State = LifecycleState.Failed)
    Assert.Equal(Some 1, harness.ExitCode())
    Assert.Equal(0, harness.StopCalls())
    Assert.Equal(0, harness.UiStarts())
    Assert.True(harness.Errors |> Seq.exists (fun error -> Object.ReferenceEquals(error, failure)))

let private verifyMissingShutdownProbeFallsBack () =
    let started = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let failure = MissingMemberException("probe missing")
    let harness =
        lifecycleWith
            (TimeSpan.FromSeconds 1.0)
            neverDelay
            (fun _ -> Error failure)
            Shell.postToDispatcher
            ignore
            (fun () -> started.Task |> Async.AwaitTask)
            (fun _ -> async.Return ())

    let first = ShutdownRequestedEventArgs()
    raiseLifetimeShutdown harness.Lifetime first
    Assert.True(first.Cancel, "a missing probe fails safe to a normal shutdown")
    let repeated = ShutdownRequestedEventArgs()
    raiseLifetimeShutdown harness.Lifetime repeated
    Assert.True(repeated.Cancel)
    Assert.Equal(1, harness.Errors.Count)

    started.SetResult(1)
    pumpUntil "the fallback startup cleanup to finish" (fun () -> harness.StopCalls() = 1)

let private verifyShutdownDuringStartup () =
    let started = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let harness = lifecycle (fun () -> started.Task |> Async.AwaitTask) (fun _ -> async.Return ())

    Assert.False(harness.Lifetime.TryShutdown(), "shutdown during startup waits for any provisioned runtime")
    Assert.Equal(LifecycleState.Stopping, harness.Lifecycle.State)
    Assert.Equal(0, harness.StopCalls())

    started.SetResult(1)
    pumpUntil "the late runtime to be stopped" (fun () -> harness.StopCalls() = 1 && harness.Lifecycle.State = LifecycleState.Stopped)
    Assert.Equal(0, harness.UiStarts())
    Assert.Equal(Some 0, harness.ExitCode())
    Assert.Empty(harness.Errors)

let private verifyCleanupAfterExitedStartupTimeout () =
    let started = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let deadline = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable exited = false
    let post callback = if not exited then Shell.postToDispatcher callback
    let harness =
        lifecycleWith
            (TimeSpan.FromSeconds 1.0)
            (fun _ -> deadline.Task |> Async.AwaitTask)
            (fun _ -> Ok false)
            post
            (fun _ -> exited <- true)
            (fun () -> started.Task |> Async.AwaitTask)
            (fun _ -> async.Return ())

    Assert.False(harness.Lifetime.TryShutdown())
    deadline.SetResult()
    pumpUntil "shutdown during hung startup to time out" (fun () -> exited)
    Assert.Equal(LifecycleState.Failed, harness.Lifecycle.State)
    Assert.Equal(Some 1, harness.ExitCode())

    started.SetResult(1)
    let clock = Stopwatch.StartNew()
    while harness.StopCalls() = 0 && clock.Elapsed < TimeSpan.FromSeconds 5.0 do Thread.Sleep 1
    Assert.Equal(1, harness.StopCalls())
    Assert.Equal(0, harness.UiStarts())

let private verifyProductionWiringStopsRuntime () =
    let lifetime = new ClassicDesktopStyleApplicationLifetime()
    let lifecycle = DesktopHost.start lifetime

    pumpUntil "the production desktop host to start" (fun () -> lifecycle.State = LifecycleState.Running)
    let runtime = lifecycle.Runtime.Value
    lifetime.MainWindow.Close()
    pumpUntil "the production desktop host to stop" (fun () -> lifecycle.State = LifecycleState.Stopped)

    Assert.Equal(1, runtime.StopRequests)

let private scenario () =
    AppBuilder
        .Configure<App>()
        .UseSkia()
        .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))
        .WithInterFont()
        .SetupWithoutStarting()
    |> ignore

    let settingsFolder = Directory.CreateTempSubdirectory "myapp-settings"

    let mutable startup: Result<Runtime, exn> option = None

    Async.StartWithContinuations(
        Runtime.start
            { PlatformEnv.live () with EnvironmentVariables = EnvironmentVariables.fromPairs [ "APPDATA", settingsFolder.FullName ] }
            Shell.postToDispatcher,
        (Ok >> Some >> fun result -> startup <- result),
        (Error >> Some >> fun result -> startup <- result),
        (fun cancelled -> startup <- Some(Error cancelled)))

    pumpUntil "the runtime to start" (fun () -> Option.isSome startup)

    let runtime =
        match startup with
        | Some(Ok runtime) -> runtime
        | Some(Error error) -> raise (Exception("Runtime startup failed", error))
        | None -> failwith "Runtime startup did not complete"

    let window, connection = Shell.create runtime (scratchFolder ())
    let vm = window.DataContext :?> MainVm
    window.Show()

    pumpUntil "the folder to list" (fun () -> vm.Entries.Count = 3)

    // Type into the real text box: the two-way binding, the debounce and the pipeline all take part.
    let filterBox = window.FindControl<TextBox> "FilterBox"
    filterBox.Focus() |> ignore
    window.KeyTextInput "read"
    pumpUntil "the filter to apply" (fun () -> vm.Entries.Count = 1 && vm.Entries[0].Name = "README.md")
    Assert.Equal("read", filterBox.Text)

    use frame = window.CaptureRenderedFrame()
    let shot = Path.Combine(Path.GetTempPath(), "myapp-headless.png")
    frame.Save shot
    Assert.True(FileInfo(shot).Length > 1000L, "the capture is not empty")

    (connection :> IDisposable).Dispose()
    window.Close()

    let mutable stopped = false
    let mutable stopError: exn option = None

    Async.StartWithContinuations(
        runtime.Stop(),
        (fun () -> stopped <- true),
        (fun error -> stopError <- Some error; stopped <- true),
        (fun cancelled -> stopError <- Some cancelled; stopped <- true))

    pumpUntil "the runtime to stop" (fun () -> stopped)
    stopError |> Option.iter (fun error -> raise (Exception("Runtime shutdown failed", error)))

    verifyFirstAndRepeatedShutdown ()
    verifyOsShutdownUsesWindowReason ()
    verifyStopFailure ()
    verifyStopTimeout ()
    verifyStartupFailure ()
    verifyMissingShutdownProbeFallsBack ()
    verifyShutdownDuringStartup ()
    verifyCleanupAfterExitedStartupTimeout ()
    verifyProductionWiringStopsRuntime ()

[<Fact>]
let ``the window lists a folder and filters it as you type`` () =
    // The headless dispatcher belongs to the thread that set it up, so the scenario runs on its own thread.
    let mutable failure: exn option = None

    let thread =
        Thread(fun () ->
            try
                scenario ()
            with error ->
                failure <- Some error)

    thread.Start()
    thread.Join()
    failure |> Option.iter (fun error -> raise (Exception("Headless scenario failed", error)))
