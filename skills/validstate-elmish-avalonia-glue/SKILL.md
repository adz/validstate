---
name: validstate-elmish-avalonia-glue
description: How to connect an Elmish program to Avalonia with Elmish.Avalonia.Glue using F# viewmodels (projection style) and no C#. Use when building or changing an Avalonia UI whose state lives in an Elmish model, writing F# viewmodels, binding lists that must keep row identity, wiring two-way text input, or delivering background results to the UI.
---

# Elmish.Avalonia.Glue with F# viewmodels

Glue keeps normal AXAML and compiled bindings while all state lives in one Elmish `Model`. Pick the **projection** style for F#: a stable F# viewmodel receives each model and copies it into bindable properties. (ElmView's write-back routing is designed around C# expression selectors; projection needs none and stays AOT-safe.)

Packages: `Elmish`, `Elmish.Avalonia.Glue`, `Elmish.Avalonia.Glue.Projection` (`Elmish.Glue.Core` comes with them).

## Project layout

- **Core** (no Avalonia reference): `Model`, `Msg`, `init`, `update`, and pure "shape" functions that derive what the screen shows (`Shape.treeRows model`, `Shape.searchStatus model`). Test these directly.
- **UI**: viewmodels, AXAML views, F# code-behind, startup.

## The viewmodel

```fsharp
[<AbstractClass>]
type Bindable() =
    inherit BindableNode()                         // Elmish.Glue.Core: INotifyPropertyChanged
    member this.Change<'T>(current: byref<'T>, next: 'T, name: string) =
        if not (EqualityComparer<'T>.Default.Equals(current, next)) then
            current <- next
            this.NotifyPropertyChanged name

type MainVm() =
    inherit Bindable()
    let mutable dispatch: Msg -> unit = ignore
    let mutable status = ""
    let mutable searchText = ""

    member _.Status = status

    member this.SearchText                         // editable: the setter dispatches, it never changes state
        with get () = searchText
        and set (value: string) =
            if value <> searchText then
                searchText <- value
                dispatch (SearchTextChanged value)

    member this.Update(model: Model) =
        this.Change(&status, Shape.searchStatus model, "Status")
        if model.Search.Query.Text <> searchText then
            searchText <- model.Search.Query.Text
            this.NotifyPropertyChanged "SearchText"

    interface IProjection<Model> with member this.Update model = this.Update model
    interface IDispatchTarget<Msg> with member _.SetDispatch target = dispatch <- target.Invoke
```

Rules:

- Raise `PropertyChanged` only when a value changed. Echoing the model back to the control that produced it then never moves a caret or resets a selection.
- Commands are a tiny F# `ICommand` that calls `dispatch msg`. They are always executable; the model decides what a message means.
- Never mutate state in a setter. Dispatch, let `update` decide, and let `Update` publish.

## Start the program

```fsharp
let postToUi =
    Action<Action>(fun action ->
        if Dispatcher.UIThread.CheckAccess() then action.Invoke()
        else Dispatcher.UIThread.Post(fun () -> action.Invoke()))

let connection =
    Elmish.Glue.Core.ElmishHost.startAndBindWithPost
        postToUi
        (Program.mkProgram (init env) (update env) (fun _ _ -> ()))
        (Action<Model>(vm.Update))
        (Action<Action<Msg>>(fun d -> (vm :> IDispatchTarget<Msg>).SetDispatch d))
```

Use `startAndBindWithPost` with an inline-when-on-UI-thread post rather than `Elmish.Avalonia.Glue.ElmishHost.startAndBind`, which always queues. With inline delivery a keystroke dispatches, updates and refreshes in one pass, so a `TwoWay` TextBox never sees a stale echo of its own text. Keep the returned connection, dispose it on exit, and use `connection.Dispatch` to drive the app from tests or startup options.

Background work (Axial flows, timers, file watchers) must post its result message to the UI thread, for example through an `env.Post` function, and dispatch there. **Never call `dispatch` from a worker thread.** Elmish's dispatch loop is single-threaded: two threads dispatching at once process messages more than once and can crash the process with a `NullReferenceException` (verified: 400,000 messages from two threads produced 474,352 and 629,785 `update` calls, and one run crashed). That includes a command's continuation on the thread pool and a callback from a search worker.

## Lists

- **Rows with identity** (tree rows, search results, chips): an `ObservableCollection<RowVm>` kept in step with `collection.SyncWith(rows: IReadOnlyList, modelKey, vmKey, create, update)`. Pass an array (`Array.ofList`), since F# lists are not `IReadOnlyList`. Keys must be unique, or `SyncWith` throws. Each row VM has its own `Update(next)` that notifies only on change.
- **Large read-only lists** (100k log lines): expose a plain array property of lightweight row objects and replace it only when its inputs change. Check those inputs by reference (`obj.ReferenceEquals(document, shownDocument)`), not structurally. Structural equality on a record holding a large array walks every element. Compute expensive per-row data lazily (`lazy`) so only realised rows pay for it, and use a `VirtualizingStackPanel`.
- **View-only effects** (scroll to a row): raise an F# event from the viewmodel (`RevealRequested`) and handle it in code-behind with `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)` before `ScrollIntoView`.

## Testing the real UI headless

```fsharp
AppBuilder.Configure<App>().UseSkia()
    .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))
    .WithInterFont().SetupWithoutStarting() |> ignore
let window, connection = Shell.create env root
window.Show()
// pump: Dispatcher.UIThread.RunJobs() in a loop until the viewmodel shows the expected state
window.KeyTextInput "query"; window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)
use frame = window.CaptureRenderedFrame() in frame.Save "shot.png"
```

Run the whole scenario on one dedicated thread, because the headless dispatcher belongs to the thread that set it up. Wait on viewmodel state with a timeout that fails with what it waited for. Never use fixed sleeps as the only synchronisation.

## Look up exact signatures

- API index for agents: https://adz.github.io/Elmish.Avalonia.Glue/llms.txt
- Docs: https://adz.github.io/Elmish.Avalonia.Glue/

[Logs Digger](https://github.com/adz/logs-digger), a log viewer built on all of these libraries, is the complete worked example these notes come from. Its `ViewModels.fs`, `Bindable.fs` and `Shell.fs` show the projection viewmodels, the change-only setter and the inline post; `tests/LogsDigger.Tests/Screenshots.fs` is the headless test.
