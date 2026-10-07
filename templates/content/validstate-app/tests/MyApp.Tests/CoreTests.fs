module MyApp.Tests.CoreTests

open System.IO
open Axial
open Xunit
open MyApp
open MyApp.Tests.Support

[<Fact>]
let ``a folder lists folders first, then files alphabetically`` () =
    use runtime = runtime ()
    let entries = runIn runtime (Folder.list (scratchFolder ()))
    Assert.Equal<string list>([ "data"; "notes.txt"; "README.md" ], entries |> List.map _.Name)
    Assert.True(entries[0].IsFolder)

[<Fact>]
let ``a missing folder is a typed failure, not an exception`` () =
    use runtime = runtime ()
    let missing = Path.Combine(Path.GetTempPath(), "myapp-does-not-exist")

    match Flow.run runtime.Env (Folder.list missing) with
    | Exit.Failure(Cause.Fail(FolderMissing path)) -> Assert.Equal(missing, path)
    | other -> failwith $"expected FolderMissing, got {other}"

[<Fact>]
let ``the filter ignores case and blank filters match everything`` () =
    let entry = { Name = "README.md"; Size = 1L; IsFolder = false }
    Assert.True(Folder.matches "readme" entry)
    Assert.True(Folder.matches "  " entry)
    Assert.False(Folder.matches "notes" entry)

[<Fact>]
let ``settings round-trip through the schema codec`` () =
    let settings = { DarkTheme = false; LastFilter = "notes" }
    Assert.Equal(Ok settings, Settings.parse (Settings.serialize settings))

[<Fact>]
let ``invalid settings are reported, not thrown`` () =
    Assert.True(Result.isError (Settings.parse "{ not json"))
    Assert.True(Result.isError (Settings.parse """{"darkTheme":"yes","lastFilter":""}"""))

[<Fact>]
let ``the pipeline answers only the newest of two quick requests`` () =
    use runtime = runtime ()
    let env = runtime.Env
    let folder = scratchFolder ()

    let answer =
        flow {
            let! listings = Hub.subscribe QueueStrategy.Unbounded env.Listings
            Queue.tryOffer { Id = 1; Folder = folder; Filter = "notes" } env.Requests |> ignore
            Queue.tryOffer { Id = 2; Folder = folder; Filter = "readme" } env.Requests |> ignore
            return! Dequeue.take listings
        }
        |> Flow.scoped

    match runIn runtime answer with
    | Listed(id, entries) ->
        Assert.Equal(2, id)
        Assert.Equal<string list>([ "README.md" ], entries |> List.map _.Name)
    | ListingFailed(_, message) -> failwith message

[<Fact>]
let ``update ignores listings for an older request`` () =
    use runtime = runtime ()
    let model, _ = App.init runtime.Env "folder" ()
    let model, _ = App.update runtime.Env (FilterChanged "a") model

    let stale, _ = App.update runtime.Env (ListingReceived(Listed(model.RequestId - 1, [ { Name = "old"; Size = 0L; IsFolder = false } ]))) model
    Assert.Empty(stale.Entries)
    Assert.Equal(Loading, stale.Status)

    let current, _ = App.update runtime.Env (ListingReceived(Listed(model.RequestId, [ { Name = "new"; Size = 0L; IsFolder = false } ]))) model
    Assert.Equal<string list>([ "new" ], current.Entries |> List.map _.Name)
    Assert.Equal(Ready, current.Status)
