namespace MyApp

open System
open System.IO
open Axial
open Axial.FileSystem

/// Checks the shipped binary end to end without a window: `myapp --self-test`. Unit tests run on the JIT;
/// this runs the paths that only fail under NativeAOT and trimming inside the published executable.
/// Add a check whenever a feature leans on reflection, globalization or a native library.
module SelfTest =
    type Check = { Name: string; Passed: bool; Detail: string }

    let private check name passed detail = { Name = name; Passed = passed; Detail = detail }

    let private checks (root: string) : Flow<AppEnv, string, Check list> =
        flow {
            let! entries = Folder.list root |> Flow.mapError string
            let filtered = entries |> List.filter (Folder.matches "NOTE")

            let settings = { Settings.defaults with DarkTheme = false; LastFilter = "notes" }
            let roundTrip = Settings.parse (Settings.serialize settings)

            // The pipeline: subscribe first, then ask, then wait for the answer.
            let! env = Flow.env
            let! listings = Hub.subscribe QueueStrategy.Unbounded env.Listings
            Queue.tryOffer { Id = 1; Folder = root; Filter = "readme" } env.Requests |> ignore
            let! event = Dequeue.take listings

            let piped =
                match event with
                | Listed(1, [ entry ]) -> Some entry.Name
                | _ -> None

            return
                [ check "folder lists through IFileSystem" (entries.Length = 3) $"{entries.Length} entries"
                  check "filter matches case-insensitively" (filtered.Length = 1) $"{filtered.Length} match"
                  check "settings round-trip through the Reified codec" (roundTrip = Ok settings) (match roundTrip with Ok _ -> "ok" | Error message -> message)
                  check "pipeline answers a request" (piped = Some "README.md") (piped |> Option.defaultValue "no answer") ]
        }
        |> Flow.scoped

    let run (env: AppEnv) : Flow<AppEnv, string, Check list> =
        flow {
            let root = Path.Combine(env.FileSystem.GetTempPath(), "myapp-self-test-" + env.FileSystem.GetRandomFileName())

            do!
                flow {
                    do! FileSystem.createDirectory (Path.Combine(root, "data"))
                    do! FileSystem.writeAllText (Path.Combine(root, "notes.txt")) "hello"
                    do! FileSystem.writeAllText (Path.Combine(root, "README.md")) "# readme"
                }
                |> Flow.mapError FileSystemError.describe

            let! results = checks root |> Flow.fold Flow.succeed (fun cause -> Flow.succeed [ check "self-test ran" false (Cause.prettyPrint id cause) ])
            do! FileSystem.deleteDirectory root true |> Flow.fold Flow.succeed (fun _ -> Flow.succeed ())
            return results
        }

    let render (results: Check list) =
        let lines = results |> List.map (fun result -> (if result.Passed then "PASS  " else "FAIL  ") + result.Name + "  (" + result.Detail + ")")
        let passed = results |> List.filter _.Passed |> List.length
        String.Join(Environment.NewLine, lines @ [ $"{passed}/{results.Length} checks passed" ])
