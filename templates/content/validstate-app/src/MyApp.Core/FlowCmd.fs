namespace MyApp

open Axial
open Elmish

/// Runs Axial flows as Elmish commands. Results come back through `post`, so every message reaches Elmish
/// on its own thread no matter which worker finished the flow.
module FlowCmd =
    let attempt post (env: 'env) (render: 'error -> string) (toMsg: Result<'value, string> -> 'msg) (work: Flow<'env, 'error, 'value>) : Cmd<'msg> =
        Cmd.ofEffect (fun dispatch ->
            async {
                let! exit = Flow.toAsync env work

                let result =
                    match exit with
                    | Exit.Success value -> Ok value
                    | Exit.Failure cause -> Error(Cause.prettyPrint render cause)

                post (fun () -> dispatch (toMsg result))
            }
            |> Async.Start)

    /// Runs a flow for its effect only; failures are ignored.
    let fireAndForget (env: 'env) (work: Flow<'env, 'error, unit>) : Cmd<'msg> =
        Cmd.ofEffect (fun _ -> Flow.toAsync env work |> Async.Ignore |> Async.Start)
