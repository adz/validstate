---
name: validstate-reified
description: How to use Reified (Reified.Schema, Constraint, Refinements, Data, Json) in F# to validate input, refine values into types that carry their invariants, read and write JSON without reflection, and parse arbitrary JSON into Data. Use when declaring a settings/config/DTO shape, validating user input, building a type that should only exist when valid, or parsing JSON lines.
---

# Reified, the validstate way

Reified turns a model's rules into data. One declaration drives validation (with paths), a compiled JSON codec, JSON Schema, and metadata. Everything is reflection-free, so it is NativeAOT- and trimming-safe.

Install `Reified.Schema` (it brings Constraint, Parse, Data and Refinements).

```fsharp
open Reified
open Reified.ConstraintDSL
open Reified.SchemaDSL
open Reified.Refinements
```

## Declare a wire shape once

```fsharp
type Settings =
    { TimeMode: string
      Zone: string
      Regex: bool }

module Settings =
    let schema =
        schema<Settings> {
            field _.TimeMode { constrain (oneOf [ "utc"; "local"; "zone" ]) }
            field _.Zone { constraints [ present; maxLength 64 ] }
            field _.Regex
            construct (fun timeMode zone regex -> { TimeMode = timeMode; Zone = zone; Regex = regex })
        }

    let private codec = Json.compile schema          // compile once

    let serialize settings = Json.serializeIndented codec settings

    /// Untrusted input (a file a person can edit): boundary parsing with every error and its path.
    let parse (text: string) =
        match Schema.parse schema (Json.parseData text) with
        | Ok settings -> Ok settings
        | Error errors ->
            errors
            |> SchemaErrors.toList
            |> List.map (fun issue -> $"{SchemaPath.format issue.Path}: {SchemaError.render issue.Error}")
            |> String.concat "; "
            |> Error
```

- Wire names are camelCased property names (`timeMode`).
- `Schema.parse` runs constraints and accumulates every failure. `Json.deserialize` (the compiled codec) checks only the wire shape and is for trusted payloads.
- `Json.parseData` throws `JsonCodecException` on malformed JSON. Wrap it when the text is untrusted.
- Test the round trip: `Assert.Equal(Ok settings, Settings.parse (Settings.serialize settings))`, plus one invalid value whose error message names the field path.

## Refine a value so invalid states cannot exist

Use a refinement when downstream code should never re-check a rule. The constructor is private; `create` is the only way in.

```fsharp
type SearchPattern = private SearchPattern of query: SearchQuery * find: (string -> struct (int * int) list)

module SearchPattern =
    let private usable =
        Constraint.customWith "a non-blank query that compiles in its search mode" (fun (query: SearchQuery) ->
            if String.IsNullOrWhiteSpace query.Text then
                Error(Violation.Atomic(AtomicViolation.Described("Type something to search for.", None)))
            else
                match regexError query with
                | Some message -> Error(Violation.Atomic(AtomicViolation.Described($"Invalid regex: {message}", None)))
                | None -> Ok())

    let refinement = Refinement.define usable construct (fun (SearchPattern(query, _)) -> query)

    let create query : Result<SearchPattern, string> =
        Refinement.create refinement query |> Result.mapError Violation.render
```

- `Refinement.define constraint construct project`. `project` must return exactly the raw value that was admitted.
- Prefer built-in constraints (`between`, `present`, `maxLength`, `oneOf`, `email`) because they carry inspectable metadata. Use `Constraint.customWith` for rules no built-in describes; the message in `AtomicViolation.Described` is what `Violation.render` shows.
- `Constraint.all [ ... ]` combines rules and reports every failure.
- A refined union that holds a function has no structural equality. Compare such values with `obj.ReferenceEquals`.

## Read arbitrary JSON

`Json.parseData` returns `Data`, a plain union you can pattern-match:

```fsharp
match Json.parseData line with
| Data.Object fields ->
    fields |> List.tryFind (fun (key, _) -> key = "@t")      // fields keep their order and duplicates
| _ -> None
// cases: Data.Null | Data.Text s | Data.Number token | Data.Bool b | Data.List items | Data.Object fields
```

Numbers keep their original token text; parse them with `Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture)`. `Json.reindent text` pretty-prints any JSON string with two-space indentation.

## F# gotchas met in practice

- If your own union has a case named `Error` (for example a log `Level`), mark the union `[<RequireQualifiedAccess>]`. Otherwise it shadows `Result.Error` in every file that opens your namespace, and Reified code that returns `Error ...` stops compiling with confusing type errors.
- Interpolated strings cannot contain string literals inside holes (`$"{f "x"}"`). Bind the value first.

## Look up exact signatures

- API index for agents: https://adz.github.io/Reified/llms.txt
- Docs: https://adz.github.io/Reified/

[Log Dug](https://github.com/adz/logdug), a log viewer built on all of these libraries, is the complete worked example these notes come from. The settings schema and the `SearchPattern` refinement above are in its `Settings.fs` and `Pattern.fs`.
