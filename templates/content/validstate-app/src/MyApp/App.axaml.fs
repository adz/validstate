namespace MyApp.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml
open MyApp

type App() =
    inherit Application()

    override this.Initialize() = AvaloniaXamlLoader.Load this

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            // Keep the host alive while the scoped services are provisioned; no frame is shown before startup completes.
            desktop.ShutdownMode <- ShutdownMode.OnExplicitShutdown

            Async.StartWithContinuations(
                Runtime.live Shell.postToDispatcher,
                (fun (runtime: Runtime) ->
                    let env = runtime.Env
                    let window, connection = Shell.create runtime (Shell.folder env (if isNull desktop.Args then [||] else desktop.Args))
                    let mutable shuttingDown = false
                    desktop.MainWindow <- window
                    desktop.ShutdownMode <- ShutdownMode.OnLastWindowClose

                    desktop.ShutdownRequested.Add(fun args ->
                        if not shuttingDown then
                            args.Cancel <- true
                            shuttingDown <- true
                            (connection :> IDisposable).Dispose()

                            Async.StartWithContinuations(
                                runtime.Stop(),
                                (fun () -> desktop.Shutdown()),
                                (fun error -> raise error),
                                (fun cancelled -> raise cancelled)))

                    window.Show()),
                (fun error -> raise error),
                (fun cancelled -> raise cancelled))
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
