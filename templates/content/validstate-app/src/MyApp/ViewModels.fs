namespace MyApp.UI

open System.Collections.ObjectModel
open Elmish.Glue.Core
open MyApp

/// One row of the listing. Rows are keyed by name, so a new listing updates rows in place
/// instead of rebuilding the list (selection and scroll survive).
type EntryVm(initial: FileEntry) =
    inherit Bindable()

    let mutable entry = initial

    static let size (bytes: int64) =
        if bytes < 1024L then $"{bytes} B"
        elif bytes < 1024L * 1024L then $"{float bytes / 1024.0:F1} KB"
        else $"{float bytes / 1024.0 / 1024.0:F1} MB"

    member _.Name = entry.Name
    member _.Kind = if entry.IsFolder then "folder" else "file"
    member _.SizeText = if entry.IsFolder then "" else size entry.Size

    member this.Update(next: FileEntry) =
        if next <> entry then
            entry <- next
            this.NotifyPropertyChanged "Kind"
            this.NotifyPropertyChanged "SizeText"

/// The window's viewmodel. Elmish owns the state: `Update` copies each new model into bindable properties,
/// and setters for editable controls dispatch messages instead of changing state.
type MainVm() =
    inherit Bindable()

    let mutable dispatch: Msg -> unit = ignore
    let entries = ObservableCollection<EntryVm>()

    let mutable filter = ""
    let mutable folder = ""
    let mutable status = ""
    let mutable isDark = true

    let refresh = Command(fun () -> dispatch Refresh)
    let toggleTheme = Command(fun () -> dispatch ToggleTheme)

    member _.Entries = entries
    member _.Folder = folder
    member _.Status = status
    member _.IsDark = isDark
    member _.RefreshCommand = refresh
    member _.ToggleThemeCommand = toggleTheme

    member _.Filter
        with get () = filter
        and set (value: string) =
            let value = if isNull value then "" else value

            if value <> filter then
                filter <- value
                dispatch (FilterChanged value)

    member this.Update(model: Model) =
        this.Change(&folder, model.Folder, "Folder")
        this.Change(&status, App.statusText model, "Status")
        this.Change(&isDark, model.Settings.DarkTheme, "IsDark")

        if model.Filter <> filter then
            filter <- model.Filter
            this.NotifyPropertyChanged "Filter"

        entries.SyncWith(
            Array.ofList model.Entries,
            (fun entry -> entry.Name),
            (fun vm -> vm.Name),
            (fun entry -> EntryVm entry),
            (fun vm entry -> vm.Update entry)
        )

    interface IProjection<Model> with
        member this.Update model = this.Update model

    interface IDispatchTarget<Msg> with
        member _.SetDispatch target = dispatch <- target.Invoke
