# Claude-facing capability registry

Weavie embeds the real Claude Code CLI and runs an "IDE" MCP server that Claude connects back to
(`Weavie.Core/Mcp/McpServer.cs`). That back-channel is the mechanism by which Weavie exposes its own
capabilities to the embedded Claude — so the user can drive Weavie by *talking to Claude* rather
than hunting through menus or memorizing a config file's shape.

The unifying idea: capabilities are **registered** in Core and automatically surfaced to Claude as
MCP tools. There are two kinds.

- **Settings** *(being built now)* — declared configuration values, each with a type, default,
  human-readable description, aliases, and validation. Surfaced as `listSettings` / `getSetting` /
  `setSetting`, which lets the user say *"set my weavie shell to nushell."* The registry's
  descriptions and aliases are what let Claude map natural language onto the exact setting key.
  Concrete design: [docs/specs/settings.md](../specs/settings.md).
- **Commands** *(not yet implemented)* — named actions Weavie can perform (e.g. "reopen the
  terminal", "open the diff panel", "split the editor"). Registered the same way and surfaced as
  invokable MCP tools, so the user can ask Claude to run them. No spec yet.

## The shared pattern

```
register(declaration)  →  Core registry  →  MCP tool surface on McpServer  →  embedded Claude
```

One declaration drives everything downstream: the MCP tool schema, validation, defaults, and the
descriptions/aliases Claude uses for natural-language mapping. Why a registry rather than
hand-written MCP tools per capability:

- **Single source of truth.** A capability is declared once; the MCP surface is generated from the
  registry, so adding a setting or command never means editing the MCP server wiring.
- **Plugin-extensible.** Future plugins contribute declarations the same way, and their
  capabilities appear to Claude automatically.
- **Format-agnostic boundary.** The Claude-facing contract is always JSON (MCP/JSON-RPC),
  regardless of how a capability is stored or implemented internally — e.g. settings persist as
  TOML, but Claude only ever sees JSON.

## Status

- Settings registry + MCP tools — in progress (see the settings spec).
- Commands registry — concept only; not yet specced or built.
