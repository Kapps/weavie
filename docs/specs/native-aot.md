# Native AOT

The macOS app, the Linux app, the headless worker, and the runner publish with Native AOT (`<PublishAot>` in their
project files). `dotnet build` stays CoreCLR for the inner loop; `dotnet publish` produces the native binaries — the
release workflow, the runner bundle, and the CI desktop tests all run that publish. Windows publishes ReadyToRun:
WinForms, WPF, and WebView2 rule out Native AOT there.

## Measurements (macOS)

Same `xcode-27` runner, published Release `osx-arm64` bundles launched alternately through the desktop
fixture, 15 warm launches each (median, ms):

| Build | Launch → welcome page | Launch → workspace page + bridge roundtrip |
| --- | --- | --- |
| JIT only | 2352 | 2963 |
| ReadyToRun | 1784 | 2336 |
| ReadyToRun with `DOTNET_ReadyToRun=0` | 2783 | 3656 |

A second same-machine run compared the shipped ReadyToRun build against Native AOT (this runner was slower
overall):

| Build | Launch → welcome page | Launch → workspace page + bridge roundtrip | Bundle |
| --- | --- | --- | --- |
| ReadyToRun | 2460 | 3191 | 248 MB |
| Native AOT | 1519 | 1997 | 71 MB |

Comparing launches across runner machines is noise-dominated; only same-machine A/B is meaningful.

## What Native AOT requires of shipped code

- **No reflection.** `src/Directory.Build.props` runs the trim, AOT, and single-file analyzers on every shipped
  project under the zero-warning gate, and sets `JsonSerializerIsReflectionEnabledByDefault=false`, so every
  platform serializes through the same metadata the AOT build uses.
- **JSON contracts.** Every serialized type has a source-generated `JsonSerializerContext`. The web message bus
  takes a `JsonTypeInfo<T>` on every handler, publish, and request (`WireJson` holds the bus contracts), so an
  unregistered or anonymous payload is a compile error. On-disk stores own nested contexts carrying their own
  options. ACP JSON-RPC payloads are `JsonObject`/`JsonNode`, written by `AcpJsonRpcWire`.
- **Deserialization defaults.** Source generation writes every `init` property, so a key missing from the JSON
  overwrites the initializer with `default`. A deserialized type that relies on an initializer default declares
  that property `set`; `[JsonExtensionData]` properties are `set` for the same reason.
- **TOML.** Tomlyn 2.x parses settings; its untyped `TomlTable` model comes from a source-generated
  `TomlSerializerContext` (`TomlDocuments`).
- **Symbols.** A Native AOT publish leaves its separate `.dbg`/`.dSYM` debug symbols (~100 MB) out of the app
  (`src/Directory.Build.targets`); the binary keeps the metadata stack traces need.
- **Helper location.** Helpers resolve under `AppContext.BaseDirectory`: `Contents/MonoBundle` under CoreCLR,
  `Contents/MacOS` under Native AOT. `ResolveHelperDirectory` (`src/HookRelay.targets`) places the hook relay, MCP
  proxy, and PTY helpers for whichever runtime the build produces.
