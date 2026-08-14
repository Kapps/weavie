# Ad-hoc inference

Weavie has two model-execution modes:

- **Agent sessions** are persistent, transcript-bearing, tool-capable runtimes attached to a worktree.
- **Ad-hoc inference** is one isolated query over exactly the typed data a feature supplies. It has no interactive
  session, resume identity, Weavie MCP connection, or target-workspace working directory.

The query asks the selected agent provider for its optional inference capability. Terminal Claude implements that
capability with `claude --print` and its normal authentication selection. Registry ACP agents do not: selecting one
returns a visible “does not support ad-hoc inference” result. There is no provider switch or hidden fallback.

Features call one internal generic API with a complete prompt, strict response `JsonTypeInfo<T>`, invocation origin,
and resource bounds. A shared prompt builder serializes typed feature context behind the same untrusted-data framing.
There is no operation registry or provider method per feature. The caller chooses a provider-neutral category:

| Category | Claude profile |
|---|---|
| `Utility` | Haiku, low effort |
| `Reasoning` | Sonnet, medium effort |

Provider model ids stay inside the Claude implementation. Weavie starts exactly one CLI process and never retries, repairs,
escalates, or switches models/providers. The installed CLI may have internal transport behavior its supported flags
do not expose; the query deadline is the outer latency bound.

Claude runs in safe mode with tools disabled, strict empty MCP configuration, no slash commands, and no session
persistence. The process runs in a private empty directory and never receives a Weavie MCP connection.

Claude receives a JSON Schema derived from the response type. Weavie independently rejects oversized, malformed,
missing, unknown, or incorrectly typed members. The feature performs semantic and authoritative validation after
typed decoding. A branch proposal must additionally pass Git syntax and collision checks; model output is never
authoritative state.
The compact new-session composer requests that proposal after 500 ms without typing and displays it in an editable
branch field before creation. New typing or manual branch input cancels the superseded CLI process.

Every non-cancellation failure takes the feature's visible failure path. Branch preview returns an empty field and
shows the failure reason while requiring manual input; it never manufactures a branch name. Caller cancellation
propagates because canceled work must not continue into a side effect.
`inference.enabled` is off by default, and automatic/event-triggered calls additionally require
`inference.allowAutomatic`. When either gate is off, the first page connection in an app run offers a persistent
notification whose **Allow** command enables and verifies both settings. Closing it changes no policy; the offer may
return after the app is relaunched.

The complete contract and first proving flow are in
[the ad-hoc inference specification](../specs/ad-hoc-inference.md).
