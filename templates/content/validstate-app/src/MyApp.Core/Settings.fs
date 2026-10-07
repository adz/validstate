namespace MyApp

open System.IO
open Axial
open Axial.FileSystem
open Axial.PlatformService
open Reified
open Reified.ConstraintDSL
open Reified.SchemaDSL

/// What the app remembers between runs. The schema is the one declaration of its shape: it validates the
/// file on load (a person can edit it) and writes it back through a compiled, reflection-free codec.
type Settings =
    { DarkTheme: bool
      LastFilter: string }

module Settings =
    let defaults = { DarkTheme = true; LastFilter = "" }

    let schema =
        schema<Settings> {
            field _.DarkTheme
            field _.LastFilter { constrain (maxLength 200) }
            construct (fun darkTheme lastFilter -> { DarkTheme = darkTheme; LastFilter = lastFilter })
        }

    let private codec = Json.compile schema

    let serialize (settings: Settings) = Json.serializeIndented codec settings

    /// Parses settings text. Malformed JSON and schema violations come back as data, never as exceptions.
    let parse (text: string) : Result<Settings, string> =
        try
            match Schema.parse schema (Json.parseData text) with
            | Ok settings -> Ok settings
            | Error errors ->
                errors
                |> SchemaErrors.toList
                |> List.map (fun issue -> $"{SchemaPath.format issue.Path}: {SchemaError.render issue.Error}")
                |> String.concat "; "
                |> Error
        with error ->
            Error error.Message

    let private folder<'env when 'env :> IHasEnvironmentVariables> : Flow<'env, Never, string> =
        flow {
            let! appData = EnvironmentVariables.tryGet "APPDATA"
            let! xdg = EnvironmentVariables.tryGet "XDG_CONFIG_HOME"
            let! home = EnvironmentVariables.tryGet "HOME"

            let baseFolder =
                appData
                |> Option.orElse xdg
                |> Option.orElse (home |> Option.map (fun home -> Path.Combine(home, ".config")))
                |> Option.defaultValue "."

            return Path.Combine(baseFolder, "MyApp")
        }

    /// Loads settings, falling back to the defaults when the file is missing or invalid.
    let load<'env when 'env :> IHasEnvironmentVariables and 'env :> IHasFileSystem> : Flow<'env, Never, Settings> =
        flow {
            let! folder = folder
            let! text = FileSystem.readAllText (Path.Combine(folder, "settings.json")) |> Flow.fold (Some >> Flow.succeed) (fun _ -> Flow.succeed None)
            return text |> Option.bind (parse >> Result.toOption) |> Option.defaultValue defaults
        }

    let save (settings: Settings) : Flow<'env, FileSystemError, unit> when 'env :> IHasEnvironmentVariables and 'env :> IHasFileSystem =
        flow {
            let! folder = folder |> Flow.widenError
            do! FileSystem.createDirectory folder
            do! FileSystem.writeAllText (Path.Combine(folder, "settings.json")) (serialize settings)
        }
