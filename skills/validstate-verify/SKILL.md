---
name: validstate-verify
description: How to prove an F# app built on Axial, Reified and Elmish actually works before calling it done, from pure tests through headless UI tests to the shipped NativeAOT binary and CI on a clean machine. Use when finishing a feature or fix, writing tests, setting up CI, publishing NativeAOT, debugging a CI-only failure, or when tempted to say "it compiles, so it works".
---

# Verify on the real thing

A build that compiles proves the types line up, nothing more. Each layer below catches a class of failure the layer above can't, and each item names a real failure it caught. Verify at the layer where the change could break, and say which layer you checked.

## 1. Pure logic: test behaviour, not wiring

Parsers, renderers, view-shaping functions and Elmish `update` are pure functions, so test them directly with values:

```fsharp
[<Fact>]
let ``stack trace lines join the entry above them`` () =
    let document = parse "2026-10-06 21:58:14 [ERR] Payment failed\nSystem.Exception: 502\n   at A.B()"
    Assert.Equal(1, document.Entries.Length)
    Assert.Equal(2, document.Entries[0].Continuation.Length)
```

- **Assert the result someone would notice**, not the calls made to get it.
- **Test `update` with plain messages and models.** It's host-neutral, so the same tests serve Glue, FuncUI, Elmish.WPF, Fabulous and Fable.
- **Use only OS-neutral paths** (`Path.Combine`, `Path.GetTempPath()`). A test with `C:\logs` in it passes on Windows and fails in Linux CI.
- **Avoid module-level values in xUnit modules** (Guardrails AXG004). Use a function that returns a fresh fixture.

## 2. Services: real I/O in a scratch folder

- **Run file and archive code against `FileSystem.live` in a temp folder** (`Directory.CreateTempSubdirectory`). This catches real path, encoding and permission behaviour that a fake would hide.
- **Prove resources are released, not just that values come back:**

  ```fsharp
  let first = collect (Files.lines node |> FlowStream.take 2)
  use exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) // throws if still open
  ```

- **Prove latest-wins and cancellation with two quick requests.** Assert that only the second request's events arrive.
- **Drive time-based code (`debounce`, `throttle`, timeouts) with a manual clock**, not real sleeps. If you must use real time, wait on a condition with a generous upper bound. Never sleep a fixed amount and assert.
- **Test the boundary on hostile input:** archive paths with a leading `/`, empty files, binary files, nanosecond timestamps, invalid regex.

## 3. The real UI, headless

Unit tests pass over a projection that is correct while the screen is wrong. Drive the real window too.

- **Avalonia, including Glue and FuncUI:**
  1. Start with `UseSkia().UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))` and `SetupWithoutStarting()`.
  2. Run the whole scenario on one dedicated thread, because the headless dispatcher belongs to it.
  3. Act through real paths: execute a row's command, `window.KeyTextInput "query"`, `window.KeyPressQwerty(PhysicalKey.Enter, ...)`.
  4. After each action, pump `Dispatcher.UIThread.RunJobs()` until a named viewmodel condition holds, with an upper bound. On timeout, fail with the condition's name: `pumpUntil "search to finish" (fun () -> ...)`.
  5. Capture `window.CaptureRenderedFrame()` and save it. Look at the image; layout, overlap and contrast bugs only show there.
- **Elmish.WPF:** the same shape, on an STA thread with a WPF dispatcher.
- **Fable/Feliz:** test `update` in .NET or Node, and the rendered DOM with a browser test runner.

Cover each user-visible flow once end to end: open, search, navigate, change a setting, live updates. The Log Dug scenario caught a two-way text box receiving stale echoes, and a scroll position lost on rebuild.

## 4. The shipped binary: a self-test inside it

Unit tests run on the JIT. NativeAOT and trimming can still break reflection-based formatting, JSON, regex, ICU time zones and native libraries in the published executable. Add a `--self-test` flag that runs without a window:

1. Write a small fixture to a temp folder (including the awkward shapes, such as a zip inside a tar.gz).
2. Start the real runtime and run each risky path once: archives, streams, codecs, time zones, the search pipeline.
3. Print one PASS or FAIL line per check, and exit non-zero on any failure.
4. On Windows, `AttachConsole(-1)` first so a GUI executable's output reaches the calling shell.

Run it on every published binary in CI and before every release. Add a check whenever a feature leans on reflection, globalization or a native library. A `--snapshot <png>` flag that renders the window after a scripted action proves the native build draws correctly, without needing a screen.

## 5. A clean machine: CI and a fresh clone

- **Reproduce CI locally in a clean container** before guessing:

  ```sh
  git clone . /tmp/ci && podman run --rm -v /tmp/ci:/src mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -c "cp -r /src /w && cd /w && dotnet test --project tests/App.Tests"
  ```

  A fresh clone catches files your machine has but the repo doesn't. A global gitignore excluding `*.log` once kept a test fixture out of the repo. A clean image catches missing native packages, such as `libfontconfig1` for SkiaSharp.
- **Add `.gitattributes` with `*.sh text eol=lf`.** A Windows checkout with `core.autocrlf` breaks bash scripts on Linux.
- **On the .NET 10 SDK, xUnit v3 needs Microsoft.Testing.Platform:** set `"test": { "runner": "Microsoft.Testing.Platform" }` in `global.json`, and run `dotnet test --project <tests>`.
- **Job logs need a login, but check-run annotations don't.** When a public CI run fails, read `/repos/<owner>/<repo>/check-runs/<job id>/annotations` before asking for logs.

## 6. Concurrency claims: measure them

When code depends on something being thread-safe, measure it rather than trusting a doc comment. Two threads dispatching into Elmish 5 for 200,000 messages each produced 474,352 and 629,785 `update` calls, and once crashed the process. That's why every background result must be posted to the Elmish thread before dispatching. Shutdown is the other trap: waiting on an Axial `App` stop from the UI thread deadlocks, so stop it from the thread pool.

## Before you say it's done

- [ ] The changed logic has a behaviour test that fails without the change.
- [ ] Services were exercised against a real temp folder, including release of resources.
- [ ] A visible change was seen in a headless capture.
- [ ] The NativeAOT binary's `--self-test` passes, if anything AOT-sensitive changed.
- [ ] CI is green, or a clean container reproduces the CI steps from a fresh clone.
- [ ] The reply says which of these were checked, and which weren't.

## Reference

[Log Dug](https://github.com/adz/logdug) has all of this in place. See `tests/LogDug.Tests/Screenshots.fs` for the headless scenario, `src/LogDug.Core/SelfTest.fs` for the self-test, `.github/workflows/` for CI and release, and `dev-docs/ReleaseProcess.md` for the release steps.
