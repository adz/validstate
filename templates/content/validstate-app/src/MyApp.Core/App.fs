namespace MyApp

open Axial
open Axial.ElmishFlows
open Elmish

type Status =
    | Loading
    | Ready
    | Failed of message: string

type Model =
    { Folder: string
      Filter: string
      /// The newest request sent; listings for older requests are ignored.
      RequestId: int
      Entries: FileEntry list
      Status: Status
      Settings: Settings }

type Msg =
    | SettingsLoaded of Settings
    | FilterChanged of string
    | Refresh
    | ListingReceived of ListingEvent
    | ToggleTheme

module App =
    let private request (runtime: Runtime) model =
        let requestId = model.RequestId + 1
        let command = { Id = requestId; Folder = model.Folder; Filter = model.Filter }

        { model with RequestId = requestId; Status = Loading },
        Cmd.ofEffect (fun _ -> Queue.tryOffer command runtime.Env.Requests |> ignore)

    let private saveSettings (runtime: Runtime) settings =
        Settings.save settings
        |> Flow.recover (fun _ -> Flow.succeed ())
        |> Cmd.OfFlow.start "save-settings" runtime.Elmish

    let init (runtime: Runtime) (folder: string) () : Model * Cmd<Msg> =
        let model =
            { Folder = folder
              Filter = ""
              RequestId = 0
              Entries = []
              Status = Loading
              Settings = Settings.defaults }

        let model, listCmd = request runtime model
        let loadSettings = Cmd.OfFlow.perform "load-settings" runtime.Elmish Settings.load SettingsLoaded
        model, Cmd.batch [ listCmd; loadSettings ]

    let update (runtime: Runtime) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        match msg with
        | SettingsLoaded settings -> { model with Settings = settings }, Cmd.none

        | FilterChanged filter ->
            let settings = { model.Settings with LastFilter = filter }
            let model, cmd = request runtime { model with Filter = filter; Settings = settings }
            model, Cmd.batch [ cmd; saveSettings runtime settings ]

        | Refresh -> request runtime model

        | ListingReceived(Listed(id, entries)) when id = model.RequestId -> { model with Entries = entries; Status = Ready }, Cmd.none
        | ListingReceived(ListingFailed(id, message)) when id = model.RequestId -> { model with Entries = []; Status = Failed message }, Cmd.none
        | ListingReceived _ -> model, Cmd.none

        | ToggleTheme ->
            let settings = { model.Settings with DarkTheme = not model.Settings.DarkTheme }
            { model with Settings = settings }, saveSettings runtime settings

    /// The program-lifetime listing pipeline is an Elmish subscription. Disposal interrupts the stream and
    /// Axial.ElmishFlows posts every result to the Elmish thread.
    let subscriptions (runtime: Runtime) (_: Model) : Sub<Msg> =
        let stream = Runtime.listings runtime.Env.Requests |> FlowStream.map ListingReceived
        Sub.OfFlowStream.subscribe [ "listings" ] runtime.Elmish stream (fun never -> Never.absurd never)

    let program (runtime: Runtime) (folder: string) =
        Program.mkProgram (init runtime folder) (update runtime) (fun _ _ -> ())
        |> Program.withSubscription (subscriptions runtime)

    let statusText (model: Model) =
        let visible = model.Entries.Length

        match model.Status with
        | Loading -> "Listing…"
        | Failed message -> message
        | Ready when visible = 1 -> "1 item"
        | Ready -> $"{visible} items"
