# validstate

Agent skills for building reliable F# software on the validstate libraries:

- [Axial](https://github.com/adz/Axial): workflows whose failures and dependencies are visible in their types.
- [Reified](https://github.com/adz/Reified): declare value and model invariants once; derive validation, parsing and codecs.
- [Elmish.Avalonia.Glue](https://github.com/adz/Elmish.Avalonia.Glue): Elmish state behind normal Avalonia AXAML.

The skills teach an agent (Claude, Codex, pi) how to use these libraries well. They cover the patterns that work, the mistakes that compile but fail at runtime, and where to look up exact signatures. They come from building [Log Dug](https://github.com/adz/logdug), an all-F# desktop app on all three. More on the libraries at [validstate.dev](https://validstate.dev).

## Skills

| Skill | Use it when |
| --- | --- |
| `validstate-axial` | Writing effects with Axial: `Flow`, `FlowStream`, `Queue`, `Hub`, `Cache`, `App`, file and clock services, Guardrails errors (AXG001-AXG006), and bridging flows into Elmish. |
| `validstate-reified` | Declaring settings, config or DTO shapes, validating input, refining values into types that carry their invariants, and parsing JSON into `Data`. |
| `validstate-elmish-avalonia-glue` | Connecting an Elmish program to Avalonia with F# viewmodels: projections, keyed lists, two-way text, and delivering background results safely. |
| `validstate-fsharp-avalonia-aot` | Building an all-F# Avalonia 12 app with ShadUI that publishes with NativeAOT. |
| `validstate-modelling` | Designing types and errors so invalid states can't be built: where each rule lives, failures as data, results through Elmish with any host (Glue, FuncUI, Elmish.WPF, Fabulous, Fable), untrusted input, time and identity. |
| `validstate-verify` | Proving the app works before calling it done: behaviour tests, real-I/O service tests, headless UI, a self-test inside the NativeAOT binary, and CI reproduced on a clean machine. |

Agents load a skill by themselves when a task matches its description. You can also ask for one by name.

## Install

**Claude Code**

```text
/plugin marketplace add adz/validstate
/plugin install validstate@validstate
```

Skills appear as `/validstate:validstate-axial` and so on. To update, refresh the marketplace with `/plugin marketplace update validstate`.

**pi**

```sh
pi install git:github.com/adz/validstate
```

Skills appear as `/skill:validstate-axial` and so on.

**Codex**

Ask Codex to run its installer:

```text
$skill-installer install the skills in github.com/adz/validstate/tree/main/skills
```

Or copy them by hand:

```sh
git clone https://github.com/adz/validstate
cp -r validstate/skills/* ~/.agents/skills/
```

Skills appear as `$validstate-axial` and so on.

**Claude.ai and the Claude desktop app**

Download this repository as a zip, then go to **Customize → Plugins → Add → Upload plugin**.

## Layout

```text
.claude-plugin/plugin.json        the plugin (Claude Code, Claude.ai)
.claude-plugin/marketplace.json   makes this repository installable as a marketplace
skills/<name>/SKILL.md            one folder per skill, in the shared Agent Skills format
```

## License

Apache-2.0
