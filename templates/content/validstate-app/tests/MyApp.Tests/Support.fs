module MyApp.Tests.Support

open System.IO
open Axial
open MyApp

/// A fresh runtime for one test. Results are delivered inline, since tests have no UI thread.
let runtime () = Runtime.start (PlatformEnv.live ()) (fun callback -> callback ())

let runIn (runtime: Runtime) (flow: Flow<AppEnv, 'error, 'value>) : 'value =
    match Flow.run runtime.Env flow with
    | Exit.Success value -> value
    | Exit.Failure cause -> failwith (Cause.prettyPrint (fun error -> error.ToString()) cause)

/// A scratch folder holding one sub-folder and two files.
let scratchFolder () =
    let folder = Directory.CreateTempSubdirectory "myapp-tests"
    Directory.CreateDirectory(Path.Combine(folder.FullName, "data")) |> ignore
    File.WriteAllText(Path.Combine(folder.FullName, "notes.txt"), "hello")
    File.WriteAllText(Path.Combine(folder.FullName, "README.md"), "# readme")
    folder.FullName
