// Service records for the app. `PlatformEnv.live` is the one place that names the live platform services.
namespace MyApp

open Axial
open Axial.FileSystem
open Axial.PlatformService

/// Ask the runtime to list a folder through a filter. Only the newest request matters.
type ListRequest =
    { Id: int
      Folder: string
      Filter: string }

/// What the listing pipeline reports, tagged with the request it answers.
type ListingEvent =
    | Listed of id: int * entries: FileEntry list
    | ListingFailed of id: int * message: string

type PlatformEnv =
    { FileSystem: IFileSystem
      Clock: IClock
      EnvironmentVariables: IEnvironmentVariables }

    interface IHasFileSystem with
        member this.FileSystem = this.FileSystem

    interface IHasClock with
        member this.Clock = this.Clock

    interface IHasEnvironmentVariables with
        member this.EnvironmentVariables = this.EnvironmentVariables

module PlatformEnv =
    let live () =
        { FileSystem = FileSystem.live
          Clock = Clock.live
          EnvironmentVariables = EnvironmentVariables.live }

/// Everything the Elmish program's effects run against. The queue and hub live in the runtime's root scope.
type AppEnv =
    { FileSystem: IFileSystem
      Clock: IClock
      EnvironmentVariables: IEnvironmentVariables
      Requests: Queue<ListRequest>
      Listings: Hub<ListingEvent>
      /// Delivers a callback to the thread that owns the Elmish loop. Elmish's dispatch is single-threaded,
      /// so every background result must arrive through this before it is dispatched.
      Post: (unit -> unit) -> unit }

    interface IHasFileSystem with
        member this.FileSystem = this.FileSystem

    interface IHasClock with
        member this.Clock = this.Clock

    interface IHasEnvironmentVariables with
        member this.EnvironmentVariables = this.EnvironmentVariables
