---
name: validstate-fsharp-avalonia-aot
description: Practical rules for an all-F# Avalonia 12 desktop app with ShadUI that publishes with NativeAOT. Use when creating an F# Avalonia project, writing AXAML for F# types, styling ShadUI controls, hitting AVLN compiler errors, or publishing/verifying a NativeAOT build.
---

# All-F# Avalonia 12 + ShadUI + NativeAOT

## Project file

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
  <IsAotCompatible>true</IsAotCompatible>
  <PublishAot>true</PublishAot>
</PropertyGroup>
<ItemGroup>
  <AvaloniaResource Include="Assets/**" />          <!-- fonts, icons -->
  <!-- Do NOT add **/*.axaml: the Avalonia SDK already includes it (AVLN2002 "Duplicate x:Class"). -->
</ItemGroup>
```

Leave `<Nullable>enable</Nullable>` off for F#. F# nullness checking floods Avalonia and Glue interop with warnings and rejects `[<AllowNullLiteral>]` on types that inherit non-nullable bases.

Code-behind in F#: `type MainWindow() as this = inherit ShadUI.Window() do AvaloniaXamlLoader.Load this`, and `App` overrides `Initialize` to call `AvaloniaXamlLoader.Load this`. Find named controls with `this.FindControl<ListBox> "EntriesList"`.

## F# cannot define Avalonia properties

XAML resolves styled and attached properties through a **public static field** (`FooProperty`). F# can only emit private static fields (`static val mutable` must be private; `static member val` is a property). Binding to an F#-defined attached property fails with AVLN3000 "Unable to find suitable setter or adder".

Work around it without C#:

- **Per-row rendering** (rich text with coloured runs): subclass the control and render from `DataContext`:

  ```fsharp
  type LogLine() =
      inherit TextBlock()
      override _.StyleKeyOverride = typeof<TextBlock>     // keep TextBlock styles
      override this.OnDataContextChanged args =
          base.OnDataContextChanged args
          this.Inlines <- match this.DataContext with :? EntryVm as e -> build e.Segments | _ -> InlineCollection()
  ```

  Container recycling changes the `DataContext`, so virtualised lists re-render correctly.
- **Dynamic style classes**: you cannot bind `Classes` to a string. Expose booleans and bind each class: `Classes.level-error="{Binding IsError}"`. Colours then live in styles that use `DynamicResource`, so theme switches restyle without code.
- `Run` elements accept classes (`run.Classes.Add "tone-string"`), so `Run.tone-string { Foreground: ... }` styles coloured inline text.

## Compiled bindings

- Put `x:DataType` on the window and on every `DataTemplate`. Types from another assembly: `xmlns:core="clr-namespace:MyApp;assembly=MyApp.Core"`.
- `ColumnDefinition` is not in the logical tree, so bindings on its `Width` have no DataContext. Set widths from code-behind when a viewmodel property changes.
- `TextBox.Watermark` is obsolete in Avalonia 12; use `PlaceholderText`. `ToggleButton` has no `BoxShadow`.

## ShadUI

- `<shadui:ShadTheme />` in `Application.Styles` (it includes `SimpleTheme` for controls ShadUI does not style, such as `TreeView`). Use `shadui:Window` for the custom title bar (`LogoContent`, `RightWindowTitleBarContent`).
- Button classes: `Primary`, `Secondary`, `Outline`, `Ghost`, `Destructive`, `Icon`. Theme brushes: `ForegroundColor`, `BackgroundColor`, `BorderColor`, `PrimaryColor`, and others listed in `Themes/Dark.axaml`.
- To restyle a control, target the **template part** ShadUI uses, not `ContentPresenter`:
  - ToggleButton checked look: `ToggleButton.x:checked /template/ Border#NameHoverBackground`.
  - ListBoxItem selection and hover: `/template/ Border#SelectionBackground` and `Border#HoverBackground`. Hide the selection tick with `ListBoxItem:selected /template/ PathIcon#CheckSelected { IsVisible: False }`.
- The `Clearable` TextBox class takes over `InnerRightContent`. If you need your own buttons there, skip `Clearable` and add a clear button yourself.
- Icons: the Lucide font (`lucide-static` on jsDelivr gives `lucide.ttf` plus `info.json` with codepoints). Reference it as `avares://<Assembly>/Assets/Fonts#lucide` and set glyphs as `Text="&#xe151;"`.

## NativeAOT

- `dotnet publish src/App -r win-x64 -c Release -o artifacts/publish`. The native link needs MSVC build tools. If it fails with `'vswhere.exe' is not recognized`, add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH`.
- Expect `IL2104`/`IL3053` aggregate warnings from **FSharp.Core** only; they come from its reflection-based printf and `%A`. Keep your own code free of `%A` and union `ToString` (Axial Guardrails AXG006 enforces this), and treat any IL warning that names your assemblies as a bug.
- Prove the native binary works instead of trusting that it compiled. Add a `--snapshot <png>` option that renders the window with `RenderTargetBitmap` after a scripted action, then exits, and look at the PNG:

  ```fsharp
  use bitmap = new RenderTargetBitmap(PixelSize(int window.Bounds.Width, int window.Bounds.Height), Vector(96.0, 96.0))
  bitmap.Render window
  bitmap.Save path
  ```

  Desktop screen capture is unreliable in automated or locked sessions (it can capture the lock-screen wallpaper), and `SendKeys` fails without an interactive desktop.

## Reference

[Logs Digger](https://github.com/adz/logs-digger), a log viewer built on all of these libraries, is the complete worked example these notes come from. Its `src/LogsDigger/` is an all-F# Avalonia 12 app on ShadUI, with the `LogLine` control above, compiled bindings throughout, a `--self-test` for NativeAOT binaries, and CI that publishes NativeAOT for Windows, Linux and macOS.
