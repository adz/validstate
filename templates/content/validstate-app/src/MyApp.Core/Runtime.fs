namespace MyApp

open System
open System.Threading
open System.Threading.Tasks
open Axial
open Axial.ElmishFlows
open Axial.Layers

/// The running Axial application behind the UI. Axial.ElmishFlows owns its root scope and delivers every
/// command and subscription result through the host's Post function.
[<Sealed>]
type Runtime internal (inner: ElmishRuntime<AppEnv>) as this =
    let mutable stopRequests = 0

    member _.Env = inner.Environment
    member internal _.Elmish = inner
    member _.StopRequests = Volatile.Read(&stopRequests)
    member _.Stop() =
        Interlocked.Increment(&stopRequests) |> ignore
        inner.Stop()

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(this.Stop() |> Async.StartAsTask)

module Runtime =
    let debounce = TimeSpan.FromMilliseconds 150.0

    let answer (request: ListRequest) : Flow<'env, Never, ListingEvent> when 'env :> Axial.FileSystem.IHasFileSystem =
        Folder.list request.Folder
        |> Flow.map (fun entries -> Listed(request.Id, entries |> List.filter (Folder.matches request.Filter)))
        |> Flow.recover (fun error -> Flow.succeed (ListingFailed(request.Id, string error)))

    /// Waits for requests to settle, then answers only the newest. Elmish owns this stream as a subscription.
    let listings (requests: Queue<ListRequest>) : FlowStream<AppEnv, Never, ListingEvent> =
        requests
        |> FlowStream.fromDequeue
        |> FlowStream.debounce debounce
        |> FlowStream.switchMapFlow answer

    let private layer : Layer<PlatformEnv, Never, AppEnv> =
        Layer.fromFlow (flow {
            let! (requests: Queue<ListRequest>) = Queue.sliding 1
            let! platform = Flow.env

            return
                { FileSystem = platform.FileSystem
                  Clock = platform.Clock
                  EnvironmentVariables = platform.EnvironmentVariables
                  Requests = requests }
        })

    /// Provisions the scoped request queue without blocking the host; Elmish starts the listing subscription.
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

            return Runtime elmishRuntime
        }

    let live post = start (PlatformEnv.live ()) post
