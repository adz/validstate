---
name: validstate-modelling
description: How to model an F# application so invalid states can't be built and failures stay data, across Reified, Axial and any Elmish host (Avalonia, Elmish.Avalonia.Glue, Avalonia.FuncUI, Elmish.WPF, Fabulous, Fable/Feliz). Use when designing domain types, deciding where a rule lives, designing error types, carrying results through Elmish messages, parsing untrusted input, or handling time and identity.
---

# Modelling for correctness

The validstate libraries share one idea: correctness should come from structure, not discipline. Put each rule where a type, a schema or an analyzer enforces it, and let every other piece of code trust it.

Two rules underlie everything below:

- **Parse at the boundary, trust inside.** Untrusted input (files, network, the user, settings on disk) becomes a typed value in one place. Code past that point never re-checks it.
- **Failures are data until the edge.** Expected failures travel as typed values through flows and messages. They become text only where a person reads them.

## Where a rule lives

| The rule | Put it in | Why |
| --- | --- | --- |
| True of every value of a concept (non-blank, valid regex, positive id) | A **Reified refinement**: private constructor, `Refinement.define`, `create` returns `Result` | The type proves it; no code downstream re-checks. |
| True only at one boundary (a form's max length, an allowed range for this API) | A **Reified constraint** on the schema field | Visible where it applies, without changing the type. |
| Between fields (start before end) | A **private record** built only through `constructResult` or a `create` function, with a public draft record for editing | No field type can carry it. |
| Which states a thing can be in (loading, showing, failed) | A **discriminated union** whose cases carry only the data valid in that state | Removes the "flag set but data missing" combinations. |
| A dependency (files, clock, environment) | An **Axial service** on the environment, enforced by Guardrails | Fakes for tests come free, and ambient calls fail the build. |

Prefer the lowest rung that prevents a real problem. A refinement that nothing downstream benefits from is a constraint dressed up as a type.

```fsharp
type ViewerState =
    | NothingOpen
    | Opening of Node
    | Showing of OpenFile                    // the document only exists once it has loaded
    | Unreadable of Node * message: string   // never "Showing with an error flag"
```

## Errors

**Each boundary gets one error union**, with cases that say what went wrong in its own terms:

```fsharp
type FilesError =
    | FileSystemFailure of message: string
    | ArchiveFailure of location: string * message: string
    | MissingEntry of location: string

    override this.ToString() =                 // required: see AOT below
        match this with
        | FileSystemFailure message -> message
        | ArchiveFailure(location, message) -> $"Could not read archive {location}: {message}"
        | MissingEntry location -> $"Archive entry not found: {location}"
```

- **Failure or defect.** An expected outcome (file missing, query invalid, remote down) is a typed failure (`Flow.fail`, `Error`). A bug or broken invariant is a defect: let it throw outside `flow { }`, or use `Flow.die` inside. Defects go to a crash path, never into the typed channel.
- **Map at the edges.** Convert a library's error into yours where you call it (`FileSystem.readAllText path |> Flow.mapError ReadFailed`), so each layer has one error type.
- **Render explicitly.** Give every error union, and any record that might be printed, a hand-written `ToString`, or a `describe` function. Generated `ToString` and `%A` use reflection that NativeAOT and trimming remove. Axial Guardrails (AXG006) rejects them in flows.
- **`Never` means it can't fail.** A flow typed `Flow<'env, Never, 'value>` has handled every failure. Use `Flow.widenError` to pass it where an error type is expected.

## Results through Elmish (any host)

This works the same with Elmish.Avalonia.Glue, Avalonia.FuncUI, Elmish.WPF, Fabulous or Fable:

- **Messages carry results, not strings:** `FileOpened of key: string * Result<LogDocument, FilesError>`. Turn the error into text in the view, not in the command.
- **Apply a result only if it is still current.** Tag requests with an id or key, and have `update` ignore a result whose id no longer matches. A later request can finish first.
- **Model loading explicitly** with a union (`Opening of Node`), not a boolean beside an option.
- **Keep the model plain data.** No cancellation sources, services or callbacks in it. They belong to the runtime or environment that runs your commands.
- **Deliver every result on the Elmish thread.** Elmish's dispatch loop is single-threaded. Background work must post its message to the loop's thread before dispatching. On Fable that's automatic, because the browser is single-threaded. On desktop hosts, it's your post function.

## Untrusted input

- **Never let a parser throw at the boundary.** Wrap anything that can (`Json.parseData` on a log line) and turn the failure into data: a skipped line, an error entry, a field error.
- **Validate structured input with `Schema.parse`.** It returns every failure with its path (`timeMode: ...`), ready to show beside the right field.
- **Use the compiled codec (`Json.compile`) only for trusted payloads** your own code wrote.
- **A settings file a person can edit is untrusted.** Parse it with the schema, fall back to defaults, and tell the user which file was ignored and why. Don't silently reset it.

## Time

- Store instants as `DateTimeOffset` in UTC. Convert to a zone only to display.
- Read the clock through Axial's `IClock` (`Clock.now`), so tests control time and Guardrails can see the dependency.
- Resolve the local time zone once at the boundary and pass it as a value. Decide explicitly what a timestamp without an offset means, and say so in the UI.

## Identity

- **Key collections by identity**, such as a path, an id or an archive entry location, never by display text or position. Keyed patching (`SyncWith`) and selection both depend on it.
- **Give composite identities a structure**, for example `Location = Disk of path | Entry of archive: Location * path`, rather than string concatenation. Derive the display string and the key from it.

## F# traps

- **A union case named `Error` or `Ok`** shadows `Result.Error`/`Result.Ok` wherever the namespace is open. Mark such unions `[<RequireQualifiedAccess>]`.
- **A parameterless generic value** (`let load : Flow<'env, ...>`) hits the value restriction. Write explicit type parameters: `let load<'env when ...> : Flow<'env, ...>`.
- **Structural equality on a record that holds a large array** walks every element. Compare such inputs by reference when deciding whether to rebuild a view.

## Checklist before a model is done

- [ ] Every rule is in a refinement, a schema constraint, a private constructor, or a union shape.
- [ ] Every boundary has one error union with a hand-written `ToString`.
- [ ] Untrusted input can't throw past the boundary.
- [ ] Messages carry typed results, and `update` ignores stale ones.
- [ ] No ambient clock, file system or environment use outside a service.
- [ ] Collections are keyed by identity.
