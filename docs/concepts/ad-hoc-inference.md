# Ad-hoc inference

Weavie has two model-execution modes:

- **Agent sessions** are persistent, transcript-bearing, tool-capable runtimes attached to a worktree.
- **Ad-hoc inference** is one isolated query over exactly the typed data a feature supplies. It has no interactive
  session, resume identity, Weavie MCP connection, or target-workspace working directory.

The query runs through the same installed agent provider selected for the surrounding action. A Codex session uses
`codex exec` and its existing Codex authentication; a Claude session uses `claude --print` and its normal Claude
authentication selection (configured API key or stored OAuth). There is no second provider setting, credential, or
automatic provider switch.

Features call one internal generic API with a complete prompt, strict response `JsonTypeInfo<T>`, invocation origin,
and resource bounds. A shared prompt builder serializes typed feature context behind the same untrusted-data framing.
There is no operation registry or provider method per feature. The caller chooses a provider-neutral category:

| Category | Codex | Claude |
|---|---|---|
| `Utility` | GPT-5.6 Luna, low effort | Haiku, low effort |
| `Reasoning` | GPT-5.6 Sol, medium effort | Sonnet, medium effort |

Provider model ids stay inside the CLI adapters. Weavie starts exactly one CLI process and never retries, repairs,
escalates, or switches models/providers. The installed CLI may have internal transport behavior its supported flags
do not expose; the query deadline is the outer latency bound.

Claude runs in safe mode with tools disabled, strict empty MCP configuration, and no session persistence. Codex
runs with its stable shell-tool feature disabled and every other built-in tool surface disabled, including apps,
browser/computer use, local-image viewing, image generation, multi-agent, plugins, workspace dependencies, and web
search. Strict config parsing makes an unsupported deny flag fail closed. The process is also ephemeral,
repo-detached, config/MCP-free, and approval-free. A per-call Codex permission profile denies all filesystem reads
and network access to model tools, including the independently registered `apply_patch` tool; the CLI itself still
reads its authentication and writes the requested structured-result file outside that tool sandbox.

Both CLIs receive a JSON Schema derived from the response type. Weavie independently rejects oversized, malformed,
missing, unknown, or incorrectly typed members. The feature performs semantic and authoritative validation after
typed decoding. A branch proposal must additionally pass Git syntax and collision checks; model output is never
authoritative state.
The compact new-session composer requests that proposal after 500 ms without typing and displays it in an editable
branch field before creation. New typing or manual branch input cancels the superseded CLI process.

Every non-cancellation failure takes the exact feature path used when inference is disabled. Caller cancellation
propagates because canceled work must not continue into a side effect. `inference.enabled` is off by default, and
automatic/event-triggered calls additionally require `inference.allowAutomatic`. Branch preview is automatic: the
visible deterministic name still works when that additional opt-in is off.

The complete contract and first proving flow are in
[the ad-hoc inference specification](../specs/ad-hoc-inference.md).
