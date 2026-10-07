namespace MyApp

open System
open System.Threading
open System.Threading.Tasks
open Axial

/// The running Axial application behind the UI. Its root scope owns the long-lived services: the request
/// queue, the listing hub and the pipeline between them. Disposing it stops the pipeline and closes the scope.
[<Sealed>]
type Runtime internal (env: AppEnv, handle: AppHandle<Never, unit>) =
    member _.Env = env

    interface IDisposable with
        member _.Dispose() =
            // Stop on the thread pool: disposal usually happens on the UI thread, whose synchronization
            // context would otherwise receive Stop's continuation while blocked waiting for it.
            Task.Run<unit>(fun () -> Async.StartAsTask(handle.Stop() |> Async.Ignore)).Wait(TimeSpan.FromSeconds 5.0)
            |> ignore

module Runtime =
    let debounce = TimeSpan.FromMilliseconds 150.0

    let private answer (request: ListRequest) : Flow<AppEnv, Never, unit> =
        flow {
            let! listings = Flow.envWith _.Listings

            let! event =
                Folder.list request.Folder
                |> Flow.map (fun entries -> Listed(request.Id, entries |> List.filter (Folder.matches request.Filter)))
                |> Flow.fold Flow.succeed (fun cause -> Flow.succeed (ListingFailed(request.Id, Cause.prettyPrint string cause)))

            do! Hub.publish event listings |> Flow.ignore
        }

    /// Waits for requests to settle, then answers only the newest. A newer request interrupts an older one.
    let pipeline: Flow<AppEnv, Never, unit> =
        flow {
            let! requests = Flow.envWith _.Requests

            return!
                requests
                |> FlowStream.fromDequeue
                |> FlowStream.debounce debounce
                |> FlowStream.switchMapFlow answer
                |> FlowStream.runDrain
        }

    /// Starts the application root and returns once its services exist.
    let start (platform: PlatformEnv) (post: (unit -> unit) -> unit) : Runtime =
        let ready = TaskCompletionSource<AppEnv>(TaskCreationOptions.RunContinuationsAsynchronously)

        let root: Flow<PlatformEnv, Never, unit> =
            flow {
                let! (requests: Queue<ListRequest>) = Queue.sliding 1
                let! (listings: Hub<ListingEvent>) = Hub.make ()

                let env =
                    { FileSystem = platform.FileSystem
                      Clock = platform.Clock
                      EnvironmentVariables = platform.EnvironmentVariables
                      Requests = requests
                      Listings = listings
                      Post = post }

                do! Flow.delay (fun () -> ready.SetResult env; Flow.succeed ())
                return! pipeline |> Flow.localEnv (fun _ -> env)
            }

        let handle = App.start platform root

        if not (ready.Task.Wait(TimeSpan.FromSeconds 10.0)) then
            invalidOp "The runtime did not start."

        new Runtime(ready.Task.Result, handle)

    let live post = start (PlatformEnv.live ()) post

    /// Runs a stream against the env and delivers each value through `Post`, until disposed.
    /// This is how Elmish subscriptions follow Axial streams.
    let follow (env: AppEnv) (stream: FlowStream<AppEnv, 'error, 'value>) (deliver: 'value -> unit) : IDisposable =
        let stop = new CancellationTokenSource()
        let work = stream |> FlowStream.runForEach (fun value -> env.Post(fun () -> deliver value))
        work.StartAsTask(env, stop.Token) |> ignore

        { new IDisposable with
            member _.Dispose() =
                stop.Cancel()
                stop.Dispose() }
