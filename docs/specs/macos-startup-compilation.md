# macOS startup compilation

The macOS app publishes with ReadyToRun (`-p:PublishReadyToRun=true` in `.github/platforms.json`), the
same startup lever as Windows and Linux. Native AOT is not used on any desktop host.

## Measurements

Same `xcode-27` runner, published Release `osx-arm64` bundles launched alternately through the desktop
fixture, 15 warm launches each (median, ms):

| Build | Launch → welcome page | Launch → workspace page + bridge roundtrip |
| --- | --- | --- |
| JIT only | 2352 | 2963 |
| ReadyToRun | 1784 | 2336 |
| ReadyToRun with `DOTNET_ReadyToRun=0` | 2783 | 3656 |

The disabled row proves the precompiled code is what's executing. Cost: the bundle grows 196 MB → 248 MB.
Comparing launches across different runner machines is noise-dominated; only same-machine A/B is meaningful.

## Native AOT blockers

A Native AOT build (`PublishAot=true`, `LinkMode=Full`) compiles to a 71 MB bundle, and WebKit works under
it — the `app://` scheme serves the UI and page→host bridge messages arrive. It is not shippable because:

- **Reflection JSON.** ~120 `JsonSerializer` call sites (Core, Hosting, ACP) use reflection metadata,
  ~70 of them with anonymous payload types. Native AOT disables reflection serialization; re-enabling it
  still fails on trimmed record constructors. Every wire and persisted type needs a `JsonSerializerContext`,
  and anonymous payloads need named types. The zero-warning gate rejects the IL2026/IL3050 warnings.
- **Base directory.** `AppContext.BaseDirectory` becomes `Contents/MacOS` instead of `Contents/MonoBundle`,
  so `HookRelay.targets`' co-location no longer matches where the host looks for `weavie-hook-relay`.
- **Tomlyn** and the minimal-API endpoints in `WorkspaceHttpServer` emit trim/AOT warnings.

The JSON migration is cross-cutting (every host shares it), so it is a prerequisite for Native AOT on any
platform, not a macOS packaging change.
