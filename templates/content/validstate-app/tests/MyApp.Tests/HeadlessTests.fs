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

let private raiseOsShutdown (lifetime: ClassicDesktopStyleApplicationLifetime) =
    let args = ShutdownRequestedEventArgs()
    let flags = BindingFlags.Instance ||| BindingFlags.NonPublic
    let property = typeof<ShutdownRequestedEventArgs>.GetProperty("IsOSShutdown", flags)
    property.SetValue(args, true)
    let eventField = typeof<ClassicDesktopStyleApplicationLifetime>.GetField("ShutdownRequested", flags)
    let handlers = eventField.GetValue(lifetime) :?> EventHandler<ShutdownRequestedEventArgs>
    handlers.Invoke(lifetime, args)
    args

let private lifecycleWithTimeout
    (stopTimeout: TimeSpan)
    (start: unit -> Async<int>)
    (stop: int -> Async<unit>) =
    let lifetime = new ClassicDesktopStyleApplicationLifetime()
    let mutable stopCalls = 0
    let mutable uiStarts = 0
    let mutable disposed = 0
    let mutable exitCode: int option = None
    let errors = ResizeArray<exn>()
    let mutable window: Window option = None

    let lifecycle =
        DesktopLifecycle<int>(
            lifetime,
            start,
            (fun runtime -> stopCalls <- stopCalls + 1; stop runtime),
            (fun _ ->
                uiStarts <- uiStarts + 1
                let created = Window()
                window <- Some created
                created, disposable (fun () -> disposed <- disposed + 1)),
            Shell.postToDispatcher,
            errors.Add,
            (fun code -> exitCode <- Some code),
            stopTimeout)

    lifecycle.Start()
    lifetime, lifecycle, (fun () -> stopCalls), (fun () -> uiStarts), (fun () -> disposed), (fun () -> exitCode), errors, (fun () -> window |> Option.iter _.Close())

let private lifecycle start stop =
    lifecycleWithTimeout (TimeSpan.FromSeconds 1.0) start stop

let private verifyFirstAndRepeatedShutdown () =
    let stopped = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let lifetime, lifecycle, stopCalls, uiStarts, disposed, exitCode, errors, close =
        lifecycle (fun () -> async.Return 1) (fun _ -> stopped.Task |> Async.AwaitTask)

    pumpUntil "the lifecycle to reach Running" (fun () -> lifecycle.State = LifecycleState.Running)
    Assert.Equal(1, uiStarts ())
    Assert.False(lifetime.TryShutdown(), "the first request is cancelled while Stop runs")
    Assert.Equal(LifecycleState.Stopping, lifecycle.State)
    Assert.Equal(1, stopCalls ())
    Assert.False(lifetime.TryShutdown(), "a repeated request is also cancelled")
    Assert.Equal(1, stopCalls ())
    Assert.Equal(None, exitCode ())

    stopped.SetResult()
    pumpUntil "the lifecycle to complete shutdown" (fun () -> lifecycle.State = LifecycleState.Stopped)
    Assert.Equal(Some 0, exitCode ())
    Assert.Equal(1, disposed ())
    Assert.Empty(errors)
    close ()

let private verifyOsShutdown () =
    let stopped = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let lifetime, lifecycle, stopCalls, _, disposed, exitCode, errors, close =
        lifecycle (fun () -> async.Return 1) (fun _ -> stopped.Task |> Async.AwaitTask)

    pumpUntil "the OS-shutdown lifecycle to reach Running" (fun () -> lifecycle.State = LifecycleState.Running)
    let args = raiseOsShutdown lifetime
    Assert.False(args.Cancel, "an OS/session shutdown must never be cancelled")
    Assert.Equal(LifecycleState.Stopping, lifecycle.State)
    Assert.Equal(1, stopCalls ())
    Assert.Equal(1, disposed ())

    stopped.SetResult()
    pumpUntil "the OS-shutdown Stop to complete" (fun () -> lifecycle.State = LifecycleState.Stopped)
    Assert.Equal(None, exitCode ())
    Assert.Empty(errors)
    close ()

let private verifyStopFailure () =
    let failure = InvalidOperationException("stop failed")
    let lifetime, lifecycle, _, _, _, exitCode, errors, close =
        lifecycle (fun () -> async.Return 1) (fun _ -> async { return raise failure })

    pumpUntil "the failing lifecycle to reach Running" (fun () -> lifecycle.State = LifecycleState.Running)
    Assert.False(lifetime.TryShutdown())
    pumpUntil "the failing lifecycle to terminate" (fun () -> lifecycle.State = LifecycleState.Failed)
    Assert.Equal(Some 1, exitCode ())
    Assert.True(errors |> Seq.exists (fun error -> Object.ReferenceEquals(error, failure)))
    close ()

let private verifyStartupFailure () =
    let failure = InvalidOperationException("startup failed")
    let _, lifecycle, stopCalls, uiStarts, _, exitCode, errors, close =
        lifecycle (fun () -> async { return raise failure }) (fun _ -> async.Return ())

    pumpUntil "the startup failure to terminate" (fun () -> lifecycle.State = LifecycleState.Failed)
    Assert.Equal(Some 1, exitCode ())
    Assert.Equal(0, stopCalls ())
    Assert.Equal(0, uiStarts ())
    Assert.True(errors |> Seq.exists (fun error -> Object.ReferenceEquals(error, failure)))
    close ()

let private verifyShutdownDuringStartup () =
    let started = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable stopped = false
    let lifetime, lifecycle, stopCalls, uiStarts, _, exitCode, errors, close =
        lifecycle (fun () -> started.Task |> Async.AwaitTask) (fun _ -> async { stopped <- true })

    Assert.False(lifetime.TryShutdown(), "shutdown during startup waits for any provisioned runtime")
    Assert.Equal(LifecycleState.Stopping, lifecycle.State)
    Assert.Equal(0, stopCalls ())

    started.SetResult(1)
    pumpUntil "the late runtime to be stopped" (fun () -> stopped && lifecycle.State = LifecycleState.Stopped)
    Assert.Equal(1, stopCalls ())
    Assert.Equal(0, uiStarts ())
    Assert.Equal(Some 0, exitCode ())
    Assert.Empty(errors)
    close ()

let private verifyShutdownDuringHungStartupTimesOut () =
    let started = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let lifetime, lifecycle, stopCalls, uiStarts, _, exitCode, errors, close =
        lifecycleWithTimeout
            (TimeSpan.FromMilliseconds 25.0)
            (fun () -> started.Task |> Async.AwaitTask)
            (fun _ -> async.Return ())

    Assert.False(lifetime.TryShutdown())
    pumpUntil "shutdown during hung startup to time out" (fun () -> lifecycle.State = LifecycleState.Failed)
    Assert.Equal(Some 1, exitCode ())
    Assert.Equal(0, stopCalls ())
    Assert.Equal(0, uiStarts ())
    Assert.True(errors |> Seq.exists (fun error -> error :? TimeoutException))

    started.SetResult(1)
    pumpUntil "the runtime that completed after timeout to be stopped" (fun () -> stopCalls () = 1)
    Assert.Equal(LifecycleState.Failed, lifecycle.State)
    Assert.Equal(Some 1, exitCode ())
    close ()

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
    verifyOsShutdown ()
    verifyStopFailure ()
    verifyStartupFailure ()
    verifyShutdownDuringStartup ()
    verifyShutdownDuringHungStartupTimesOut ()

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
