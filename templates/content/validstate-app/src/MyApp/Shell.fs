namespace MyApp.UI

open System
open Avalonia.Threading
open Elmish.Glue.Core
open MyApp

/// Builds the window and connects it to the Elmish program. Shared by the app and the headless tests.
module Shell =
    let postToDispatcher (callback: unit -> unit) = Dispatcher.UIThread.Post(fun () -> callback ())

    /// The folder to list: the first command-line argument, else the current directory.
    let folder (env: AppEnv) (args: string array) =
        let chosen =
            match args |> Array.tryHead with
            | Some path when env.FileSystem.DirectoryExists path -> path
            | _ -> env.FileSystem.GetCurrentDirectory()

        env.FileSystem.TrimEndingDirectorySeparator(env.FileSystem.GetFullPath chosen)

    /// Runs updates inline when already on the UI thread. A keystroke then dispatches, updates, and refreshes
    /// the viewmodel in one pass, so a two-way TextBox never sees a stale echo of its own text.
    let private postToUi =
        Action<Action>(fun action ->
            if Dispatcher.UIThread.CheckAccess() then action.Invoke()
            else Dispatcher.UIThread.Post(fun () -> action.Invoke()))

    let create (env: AppEnv) (folder: string) : MainWindow * ElmishHostConnection<Msg> =
        let vm = MainVm()
        let window = MainWindow()
        window.Attach vm

        let connection =
            ElmishHost.startAndBindWithPost
                postToUi
                (App.program env folder)
                (Action<Model>(vm.Update))
                (Action<Action<Msg>>(fun dispatch -> (vm :> IDispatchTarget<Msg>).SetDispatch dispatch))

        window, connection
