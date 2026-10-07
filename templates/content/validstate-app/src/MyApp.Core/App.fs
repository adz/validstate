namespace MyApp

open Axial
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
    let private request env model =
        let requestId = model.RequestId + 1
        let command = { Id = requestId; Folder = model.Folder; Filter = model.Filter }

        { model with RequestId = requestId; Status = Loading },
        Cmd.ofEffect (fun _ -> Queue.tryOffer command env.Requests |> ignore)

    let private saveSettings env settings =
        Settings.save settings |> FlowCmd.fireAndForget env

    let init (env: AppEnv) (folder: string) () : Model * Cmd<Msg> =
        let model =
            { Folder = folder
              Filter = ""
              RequestId = 0
              Entries = []
              Status = Loading
              Settings = Settings.defaults }

        let model, listCmd = request env model

        let loadSettings =
            Settings.load
            |> FlowCmd.attempt env.Post env (fun (never: Never) -> Never.absurd never) (fun result ->
                SettingsLoaded(Result.defaultValue Settings.defaults result))

        model, Cmd.batch [ listCmd; loadSettings ]

    let update (env: AppEnv) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        match msg with
        | SettingsLoaded settings -> { model with Settings = settings }, Cmd.none

        | FilterChanged filter ->
            let settings = { model.Settings with LastFilter = filter }
            let model, cmd = request env { model with Filter = filter; Settings = settings }
            model, Cmd.batch [ cmd; saveSettings env settings ]

        | Refresh -> request env model

        | ListingReceived(Listed(id, entries)) when id = model.RequestId -> { model with Entries = entries; Status = Ready }, Cmd.none
        | ListingReceived(ListingFailed(id, message)) when id = model.RequestId -> { model with Entries = []; Status = Failed message }, Cmd.none
        | ListingReceived _ -> model, Cmd.none

        | ToggleTheme ->
            let settings = { model.Settings with DarkTheme = not model.Settings.DarkTheme }
            { model with Settings = settings }, saveSettings env settings

    /// Listings arrive from the runtime's hub as an Elmish subscription, posted to the Elmish thread.
    let subscriptions (env: AppEnv) (_: Model) : Sub<Msg> =
        [ [ "listings" ], fun dispatch -> Runtime.follow env (FlowStream.fromHub QueueStrategy.Unbounded env.Listings) (ListingReceived >> dispatch) ]

    let program (env: AppEnv) (folder: string) =
        Program.mkProgram (init env folder) (update env) (fun _ _ -> ())
        |> Program.withSubscription (subscriptions env)

    let statusText (model: Model) =
        let visible = model.Entries.Length

        match model.Status with
        | Loading -> "Listing…"
        | Failed message -> message
        | Ready when visible = 1 -> "1 item"
        | Ready -> $"{visible} items"
