# MyApp

A desktop app in F# on [Avalonia](https://avaloniaui.net) and [ShadUI](https://github.com/accntech/shad-ui), built with the validstate libraries:

- [Elmish](https://elmish.github.io/elmish/) holds all state, connected to Avalonia by [Elmish.Avalonia.Glue](https://github.com/adz/Elmish.Avalonia.Glue) with F# viewmodels.
- [Axial](https://github.com/adz/Axial) runs every effect. [Axial.Guardrails](https://adz.github.io/Axial/) fails the build on direct file, clock or environment access.
- [Reified](https://github.com/adz/Reified) declares the settings file once, for validation and its JSON codec.

It starts as a small working example: a window that lists the current folder, with a filter as you type. Replace the example with your app and keep the structure.

## Layout

| Path | What it holds |
| --- | --- |
| `src/MyApp.Core` | Everything testable without a window. `Folder.fs` lists through Axial's file service. `Settings.fs` is the Reified schema. `Runtime.fs` is the Axial root that owns long-lived services (a request queue, a result hub, and the debounced latest-wins pipeline between them). `App.fs` is the Elmish model, messages, update and subscriptions. `SelfTest.fs` holds the NativeAOT checks. |
| `src/MyApp` | The Avalonia app. `ViewModels.fs` holds the F# viewmodels, `Shell.fs` connects Elmish to the window, and `Views/` holds the AXAML. |
| `tests/MyApp.Tests` | Behaviour and real-I/O tests, plus a headless scenario that types into the real window. |
| `.github/workflows` | CI on every push and pull request. A `vX.Y.Z` tag builds NativeAOT releases for Windows, Linux and macOS. |

## Run, test, publish

```sh
dotnet run --project src/MyApp            # lists the current folder; pass another folder as an argument
dotnet test --project tests/MyApp.Tests
bash scripts/publish.sh                   # NativeAOT for this machine, into artifacts/publish/myapp
artifacts/publish/myapp/myapp --self-test
```

On Linux, NativeAOT needs `clang` and `zlib1g-dev`, and the headless tests need `libfontconfig1`. On Windows it needs the MSVC build tools, which the publish script finds through `vswhere`.

## Release

Commit and push, then `git tag v0.1.0 && git push origin v0.1.0`. The release workflow builds on each platform, runs every binary's `--self-test`, and publishes zips, tarballs, a Windows installer and checksums to a GitHub release. Release notes come from `dev-docs/releases/X.Y.Z.md` when that file exists.

## Rules this project keeps

- **Effects go through Axial services.** Guardrails turns a direct `File`, `DateTime.Now` or `Environment` call into a build error.
- **Background results reach Elmish through `AppEnv.Post`.** Elmish's dispatch loop is single-threaded, and dispatching from a worker thread corrupts it.
- **Error types override `ToString`**, so they render under NativeAOT.
- **Every AXAML binding is compiled** (`x:DataType`).

The [validstate skills](https://github.com/adz/validstate) teach an AI agent these rules and the libraries.
