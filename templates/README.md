# validstate templates

`dotnet new` templates for F# apps on the validstate libraries.

```sh
dotnet new install Validstate.Templates
dotnet new validstate-app -n MyTool --publisher "Your Name" --repository you/mytool
```

## validstate-app

An all-F# desktop app on Avalonia 12 and ShadUI that starts as a working example (a folder listing with a filter as you type) and comes with:

- Elmish state, connected to Avalonia by Elmish.Avalonia.Glue with F# viewmodels and thread-safe delivery;
- Axial services for every effect, with Axial.Guardrails at error severity;
- an Axial runtime root with a request queue, a result hub, and a debounced, latest-wins stream pipeline;
- Reified settings validated on load and written through a compiled codec;
- behaviour, real-I/O and headless UI tests;
- NativeAOT publishing with a `--self-test` inside the binary;
- GitHub Actions CI, plus tag-driven releases for Windows, Linux and macOS with an installer.

`-n` sets the project name (`MyTool.Core`, `MyTool`, `MyTool.Tests`) and the command (`mytool`). `--publisher` and `--repository` fill in the installer and README.

## Building the package

```sh
dotnet pack templates/Validstate.Templates.proj -o artifacts
dotnet new install artifacts/Validstate.Templates.0.1.0.nupkg
```
