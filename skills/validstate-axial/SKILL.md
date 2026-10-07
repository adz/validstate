---
name: validstate-axial
description: How to write F# effects with Axial (Flow, FlowStream, Queue, Hub, Cache, App, Axial.FileSystem, Axial.PlatformService) under Axial.Guardrails at error severity. Use when writing or reviewing F# code that reads files, the clock or environment variables, streams or searches data, runs background or long-lived work, designs an Axial service, or bridges flows into Elmish; when a build fails with AXG001-AXG006; or when adding Axial to a project.
---

# Axial, the validstate way

Axial makes a workflow's dependencies and expected failures part of its type: `Flow<'env, 'error, 'value>`. A flow is a cold description; nothing runs until a boundary starts it.

## Set up a project

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <AxialGuardrailsSeverity>error</AxialGuardrailsSeverity>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Axial.Guardrails" PrivateAssets="all" />
</ItemGroup>
```

Reference `Axial`, plus `Axial.FileSystem` and `Axial.PlatformService` when you touch files, the clock, or environment variables. Referencing a package switches its guardrail on.

Prove the analyzer is live before trusting a clean build. Add a throwaway file that calls `System.IO.File.Exists "x"` and `System.DateTime.Now`, build, expect two `AXG001` errors, then delete the file.

## Model the environment as one record of services

```fsharp
type AppEnv =
    { FileSystem: IFileSystem
      Clock: IClock
      EnvironmentVariables: IEnvironmentVariables
      Files: IFiles                     // your own services sit beside Axial's
      Post: (unit -> unit) -> unit }    // UI-thread delivery for Elmish results
    interface IHasFileSystem with member this.FileSystem = this.FileSystem
    interface IHasClock with member this.Clock = this.Clock
    interface IHasEnvironmentVariables with member this.EnvironmentVariables = this.EnvironmentVariables
    interface IHasFiles with member this.Files = this.Files
```

Services that own state (caches, queues, hubs) are built by a flow inside a long-lived scope; see "Host long-lived services" below.

Define your own `IHasX` interface for each app service, so library-style code can ask for `'env when 'env :> IHasFileSystem and 'env :> IHasArchives` rather than the concrete record. The live record is the only place that names implementations. Tests use the same record with `{ AppEnv.live () with EnvironmentVariables = EnvironmentVariables.fromPairs [ "APPDATA", temp ] }`.

## Write flows

```fsharp
let readConfig (path: string) : Flow<'env, ConfigError, Config> when 'env :> IHasFileSystem =
    flow {
        let! text = FileSystem.readAllTextAsync path |> Flow.mapError ReadFailed
        let! config = Flow.attemptBlocking (fun _ -> parse text) |> Flow.mapError ParseFailed
        return config
    }
```

- `FileSystem.*` returns `Flow<'env, FileSystemError, _>`. Map it into your own error union at once.
- `Flow.fromBlocking` runs CPU-heavy or synchronous work on the thread pool; thrown exceptions become defects. `Flow.attemptBlocking` puts them in the typed error channel as `exn`.
- `Flow.orElseWith` recovers but keeps the same error type. To recover completely into `Never` (for example, "missing file means defaults"), use `Flow.fold (Some >> Flow.succeed) (fun _ -> Flow.succeed None)`.
- `Flow.widenError` lifts a `Flow<_, Never, _>` into any error type.
- `for x in xs do do! step x` works inside `flow { }`, including recursion (`let rec visit node = flow { ... do! visit child }`).
- Give each error union a hand-written `ToString`. Guardrails AXG006 rejects `string error` or `$"{error}"` otherwise, because the generated `ToString` uses reflection that NativeAOT removes.

### Value restriction

A flow with no parameters and generic `'env` is a value, and F# rejects it. Make the type parameter explicit:

```fsharp
let load<'env when 'env :> IHasEnvironmentVariables and 'env :> IHasFileSystem> : Flow<'env, Never, Settings> = flow { ... }
```

## Run flows at the boundary

- `Flow.run env flow` blocks and returns `Exit<'value, 'error>` (tests, scripts).
- `Flow.toAsync env flow` is cold; use it for Elmish commands.
- `flow.StartAsTask(env, cancellationToken)` starts now with a token you control. Use it for cancellable work such as a search that a newer search replaces.
- Render failures with `Cause.prettyPrint (fun e -> e.ToString()) cause`.

### Elmish bridge

Long-running streams become Elmish subscriptions: start the stream against the env with a cancellation token and post each value back.

```fsharp
let follow env (stream: FlowStream<AppEnv, 'e, 'v>) (deliver: 'v -> unit) : IDisposable =
    let stop = new CancellationTokenSource()
    (stream |> FlowStream.runForEach (fun v -> env.Post(fun () -> deliver v))).StartAsTask(env, stop.Token) |> ignore
    { new IDisposable with member _.Dispose() = stop.Cancel() }

let subscriptions env model : Sub<Msg> =
    [ [ "search-events" ], fun dispatch -> follow env (FlowStream.fromHub QueueStrategy.Unbounded env.SearchEvents) (SearchEventReceived >> dispatch) ]
```

One-shot commands use `Flow.toAsync`:

```fsharp
let attempt post env render toMsg (work: Flow<'env, 'error, 'value>) : Cmd<'msg> =
    Cmd.ofEffect (fun dispatch ->
        async {
            let! exit = Flow.toAsync env work
            let result =
                match exit with
                | Exit.Success value -> Ok value
                | Exit.Failure cause -> Error(Cause.prettyPrint render cause)
            post (fun () -> dispatch (toMsg result))
        } |> Async.Start)
```

Post results back to the thread that owns the Elmish loop. Elmish dispatch is not designed for concurrent callers.

For a debounce, sleep through the clock service instead of `Task.Delay` (AXG001): `Flow.sleep (TimeSpan.FromMilliseconds 220.0)` and then dispatch a message that carries a generation number, so stale timers are ignored.

## Prefer streams for anything that is a sequence

If work produces many values over time (walking a tree, reading a file's lines, search results, file changes, progress), write it as a `FlowStream` rather than a recursive flow with a callback and a mutable counter. Streams give you back-pressure, early stop, parallelism and cleanup for free:

```fsharp
let lines node : FlowStream<'env, FilesError, string> =
    FlowStream.using (reader node) (fun reader ->            // released when the consumer stops, however it stops
        FlowStream.repeatFlow (Flow.fromBlocking (fun _ -> readBatch reader))
        |> FlowStream.takeWhile (fun batch -> batch.Length > 0)
        |> FlowStream.collect FlowStream.fromSeq)            // batch syscalls; one Flow per line is slow

let walk descend root =                                      // lazy: a folder is listed only when pulled past
    FlowStream.unfoldFlow (function
        | [] -> Flow.succeed None
        | node :: rest when isContainer node && descend node ->
            children node |> Flow.fold (fun found -> Flow.succeed (Some(node, found @ rest))) (fun _ -> Flow.succeed (Some(node, rest)))
        | node :: rest -> Flow.succeed (Some(node, rest))) [ root ]

let search pattern root =
    walk descend root
    |> FlowStream.filter isSearchable
    |> FlowStream.indexed                                     // keep tree order for display
    |> FlowStream.mapFlowPar (Parallelism.bounded 4) (fun (order, node) -> fileHits pattern order node)
    |> FlowStream.scan (fun (totals, _) found -> next totals found) (empty, false)
    |> FlowStream.takeWhile (fun (_, capped) -> not capped)   // ending the stream stops the walk and in-flight files
```

Useful operators: `debounce` + `switchMapFlow` for search-as-you-type (a newer value interrupts the running flow), `groupedWithin size window` to batch UI updates, `throttle` for progress or file-change bursts, `runTryHead` to stop at the first value. `mapFlowPar` emits in completion order; carry an index if order matters.

## Host long-lived services in an application root

`Cache`, `Queue`, `Hub` and `Hub.subscribe` belong to the scope they were made in. A `Flow.run` per command closes its scope when it returns, so services made there die with it. Make them once in an `App.start` root and keep the root running:

```fsharp
let start (platform: PlatformEnv) : Runtime =
    let ready = TaskCompletionSource<AppEnv>(TaskCreationOptions.RunContinuationsAsynchronously)
    let root =
        flow {
            let! files = Files.make                     // makes its Cache here, in the root scope
            let! commands = Queue.sliding 1             // only the newest request matters
            let! events = Hub.make ()
            let env = { ... Files = files; SearchCommands = commands; SearchEvents = events }
            do! Flow.delay (fun () -> ready.SetResult env; Flow.succeed ())
            return! searchPipeline |> Flow.localEnv (fun _ -> env)   // runs until the root stops
        }
    let handle = App.start platform root
    new Runtime(ready.Task.Result, handle)
```

- Commands from UI code go in with the synchronous `Queue.tryOffer`. Results go out through a `Hub`; consumers follow it with `FlowStream.fromHub`.
- `Cache.make` needs `'env :> IHasClock`. A cache whose lookup needs the cache itself (nested archives) can hold it in a `ref` set right after `Cache.make`. `Cache` has no expiry or size cap; keep a small LRU of keys and `Cache.invalidate` the oldest.
- **Stop the root off the UI thread.** `Async.RunSynchronously(handle.Stop(), timeout)` called on the UI thread deadlocks, because Stop's continuation is posted to the UI thread's synchronization context, and the timeout does not fire. Use `Task.Run<unit>(fun () -> Async.StartAsTask(handle.Stop() |> Async.Ignore)).Wait(TimeSpan.FromSeconds 5.0)`.

## Designing an Axial service

Keep the interface to the primitives that need platform access or shared state, and compose everything else in the module, so a fake implements a few members:

```fsharp
type IFiles =
    abstract Children: Node -> Flow<unit, FilesError, Node list>
    abstract OpenRead: Location -> Flow<unit, FilesError, Stream>
    abstract Watch: Location -> FlowStream<unit, FilesError, FileChange>
type IHasFiles = abstract Files: IFiles

[<RequireQualifiedAccess>]
module Files =
    let service<'env, 'error when 'env :> IHasFiles> : Flow<'env, 'error, IFiles> = Flow.envWith _.Files
    let children node : Flow<'env, FilesError, Node list> when 'env :> IHasFiles =
        flow { let! files = service in return! files.Children node |> Flow.localEnv ignore }
    let readText, reader, lines, walk, watch = ...        // built from the three primitives
    let make<'env when 'env :> IHasFileSystem and 'env :> IHasClock> : Flow<'env, Never, IFiles> = ...
```

Interface members take `unit` as their environment because the live implementation captures what it needs when `make` runs; `Flow.localEnv ignore` adapts them to any caller. Wrap platform callbacks (a `FileSystemWatcher`) as a `Resource` that owns the handle, feed a `Queue` with `Queue.tryOffer` from the callback, and expose `FlowStream.fromDequeue` inside `FlowStream.using`.

## Guardrail quick reference

| Code | Meaning | Fix |
| --- | --- | --- |
| AXG001 | Direct ambient effect (File, Directory, DateTime.Now, Stopwatch, Task.Delay, Thread.Sleep, Environment, Console, Random, Guid.NewGuid) | Use the service (`FileSystem.*`, `Clock.now`, `Flow.sleep`, `EnvironmentVariables.tryGet`). At a real boundary write `// axial-allow-effect: <category>` on or above the line. |
| AXG002 | An allow directive that suppresses nothing | Delete it. |
| AXG003 | `raise`/`failwith` inside `flow { }` | `return! Flow.fail error`, or `Flow.die` for a defect. |
| AXG004 | Module-level `let` value in an xUnit test module | Make it a function: `let fixture () = ...`. |
| AXG005 | `fun _ -> task` discarding the cancellation token in `Flow.fromTask`/`ColdTask` | Pass the token through. |
| AXG006 | Reflection formatting (`%A`, `string` on a union or record without a `ToString` override) | Override `ToString`, or format fields explicitly. |

`TimeZoneInfo.Local` and `System.IO.Path` are not flagged. `IFileSystem` members (`fs.DirectoryExists`, `fs.GetCurrentDirectory()`) are the sanctioned way to do synchronous checks at a boundary.

## Look up exact signatures

Before guessing an overload or a constraint, read the API reference. Hidden requirements (for example `Cache.make` needing `'env :> IHasClock`) show up there and in compile errors, not in the type at a glance.

- API index for agents: https://adz.github.io/Axial/llms.txt
- Docs: https://adz.github.io/Axial/

[Logs Digger](https://github.com/adz/logs-digger), a log viewer built on all of these libraries, is the complete worked example these notes come from. The `Files` service, the runtime root and the search pipeline sketched above are in its `src/LogsDigger.Core/Files/`, `Runtime.fs` and `Search.fs`.
