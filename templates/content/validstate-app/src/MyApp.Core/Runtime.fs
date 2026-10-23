namespace MyApp

open System
open System.Threading.Tasks
open Axial
open Axial.ElmishFlows
open Axial.Layers

/// The running Axial application behind the UI. Axial.ElmishFlows owns its root scope and delivers every
/// command and subscription result through the host's Post function.
[<Sealed>]
type Runtime internal (inner: ElmishRuntime<AppEnv>) =
    member _.Env = inner.Environment
    member internal _.Elmish = inner
    member _.Stop() = inner.Stop()

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(inner.Stop() |> Async.StartAsTask)

module Runtime =
    let debounce = TimeSpan.FromMilliseconds 150.0

    let answer (request: ListRequest) : Flow<'env, Never, ListingEvent> when 'env :> Axial.FileSystem.IHasFileSystem =
        Folder.list request.Folder
        |> Flow.map (fun entries -> Listed(request.Id, entries |> List.filter (Folder.matches request.Filter)))
        |> Flow.recover (fun error -> Flow.succeed (ListingFailed(request.Id, string error)))

    /// Waits for requests to settle, then answers only the newest. A newer request interrupts an older one.
    let pipeline: Flow<AppEnv, Never, unit> =
        flow {
            let! requests = Flow.envWith _.Requests
            let! listings = Flow.envWith _.Listings

            return!
                requests
                |> FlowStream.fromDequeue
                |> FlowStream.debounce debounce
                |> FlowStream.switchMapFlow answer
                |> FlowStream.mapFlow (fun event -> Hub.publish event listings |> Flow.ignore)
                |> FlowStream.runDrain
        }

    let private layer : Layer<PlatformEnv, Never, AppEnv> =
        Layer.fromFlow (flow {
            let! (requests: Queue<ListRequest>) = Queue.sliding 1
            let! (listings: Hub<ListingEvent>) = Hub.make ()
            let! platform = Flow.env

            return
                { FileSystem = platform.FileSystem
                  Clock = platform.Clock
                  EnvironmentVariables = platform.EnvironmentVariables
                  Requests = requests
                  Listings = listings }
        })

    /// Provisions the scoped services without blocking the host, then starts the long-lived pipeline.
    let start (platform: PlatformEnv) (post: (unit -> unit) -> unit) : Async<Runtime> =
        async {
            let options =
                { Post = post
                  OnDefect = (fun defect -> post (fun () -> raise (InvalidOperationException defect.Rendered)))
                  HistoryCapacity = 32 }

            let! started = ElmishRuntime.startLayer options layer platform

            let elmishRuntime =
                match started with
                | Ok runtime -> runtime
                | Error never -> Never.absurd never

            for startPipeline in Cmd.OfFlow.start "listing-pipeline" elmishRuntime pipeline do
                startPipeline ignore

            return Runtime elmishRuntime
        }

    let live post = start (PlatformEnv.live ()) post
