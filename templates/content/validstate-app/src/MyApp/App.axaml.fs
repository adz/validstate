namespace MyApp.UI

open System
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml
open MyApp

type App() =
    inherit Application()

    override this.Initialize() = AvaloniaXamlLoader.Load this

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let runtime = Runtime.live Shell.postToDispatcher
            let env = runtime.Env
            let window, connection = Shell.create env (Shell.folder env (if isNull desktop.Args then [||] else desktop.Args))
            desktop.MainWindow <- window

            desktop.Exit.Add(fun _ ->
                (connection :> IDisposable).Dispose()
                (runtime :> IDisposable).Dispose())
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
