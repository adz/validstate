module MyApp.Tests.HeadlessTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Avalonia
open Avalonia.Controls
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

let private scenario () =
    AppBuilder
        .Configure<App>()
        .UseSkia()
        .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))
        .WithInterFont()
        .SetupWithoutStarting()
    |> ignore

    let settingsFolder = Directory.CreateTempSubdirectory "myapp-settings"

    use runtime =
        Runtime.start
            { PlatformEnv.live () with EnvironmentVariables = EnvironmentVariables.fromPairs [ "APPDATA", settingsFolder.FullName ] }
            Shell.postToDispatcher

    let window, connection = Shell.create runtime.Env (scratchFolder ())
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
