namespace MyApp.UI

open Avalonia
open Avalonia.Markup.Xaml
open Avalonia.Styling

type MainWindow() as this =
    inherit ShadUI.Window()

    do AvaloniaXamlLoader.Load this

    member this.Attach(vm: MainVm) =
        this.DataContext <- vm

        let applyTheme () =
            match Application.Current with
            | null -> ()
            | app -> app.RequestedThemeVariant <- if vm.IsDark then ThemeVariant.Dark else ThemeVariant.Light

        applyTheme ()

        vm.PropertyChanged.Add(fun args ->
            if args.PropertyName = "IsDark" then
                applyTheme ())
