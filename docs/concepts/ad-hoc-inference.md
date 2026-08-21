# Ad-hoc inference

Weavie has two model-execution modes:

- **Agent sessions** are persistent, transcript-bearing, tool-capable runtimes attached to a worktree.
- **Ad-hoc inference** is one isolated query over exactly the typed data a feature supplies. It has no interactive
  session, resume identity, Weavie MCP connection, or persisted transcript.

## Session-owned by construction

Inference is never routed independently of the work it is about. Every query names an `InferenceOwner` — the
session's agent provider plus its worktree root — and the service derives both the provider and the working
directory from it. There is no `inference.provider` setting and no provider picker: the agent that renders your
session is the agent that answers questions about it.

The query runs **in the owning worktree**, not a scratch directory. Inference is about the user's code, so the agent
sees the repository it is reasoning over — including whatever conventions `AGENTS.md` already documents. Keeping the
working directory stable across queries also keeps the provider's cached prompt prefix intact.

Features call one internal generic API with complete text and image input, strict response `JsonTypeInfo<T>`,
invocation origin, and text, image-count, aggregate-image, output, and time bounds. Images remain provider-native
content rather than being serialized into the text prompt. A shared prompt builder serializes typed feature context
behind the same untrusted-data framing.
The caller chooses a provider-neutral category (`Utility` or `Reasoning`); provider model ids stay inside the
provider.

## Two providers, one contract

**Terminal Claude** runs `claude --print` in safe mode with tools disabled, strict MCP configuration, no slash
commands, no session persistence, and a JSON Schema derived from the response type. Query images are materialized
under Weavie's owner-only internal state for the process lifetime and supplied through Claude's image-path prompt
convention.

**Any ACP agent** runs one transient process, one throwaway session, and one prompt turn. Images use native ACP
content blocks when the agent advertises `promptCapabilities.image`; otherwise the query fails visibly instead of
discarding them. Isolation is structural rather than declarative: Weavie advertises no client capabilities, passes
no MCP servers, and refuses every agent request — filesystem, terminal, permission, and elicitation alike. An agent
with nothing to reach for makes no tool calls.

ACP has no output-schema field, so the schema travels in the prompt and Weavie enforces it locally: the reply must be
exactly one JSON value, or the query fails. Prose, explanations, and markdown fences are rejected rather than
salvaged.

Weavie starts exactly one process per query and never retries, repairs, escalates, or switches models or providers.
The query deadline is the outer latency bound.

## Weavie does not choose models

ACP exposes model and reasoning-level selectors — `configOptions` carries the reserved `model` and `thought_level`
categories — but no cost, capability, or ordering semantics for their values. Option ids and value ids are
agent-defined opaque strings, presence is not guaranteed, and the option set changes when another option changes. No
amount of probing recovers price, because ACP reports tokens and never a rate.

So Weavie doesn't try. Every category runs the agent's own configured model at its own configured effort, and the
receipt reports which model answered. Measurement on the two shipped registry agents showed the defaults are already
right for a fifty-token query; overriding them cost a fresh cache lineage and, on a small model, produced slower,
longer, unparseable output.

## Failures are values

Every non-cancellation failure takes the feature's visible failure path with a reason that names the actual cause —
an unparseable reply, an authentication demand, an agent that never started. Branch preview returns an empty field
and shows that reason while requiring manual input; it never manufactures a branch name. Caller cancellation
propagates, because canceled work must not continue into a side effect.

`inference.enabled` is off by default, and automatic/event-triggered calls additionally require
`inference.allowAutomatic`. When either gate is off, the first page connection in an app run offers a persistent
notification whose **Allow** command enables and verifies both settings.

The complete contract and first proving flow are in
[the ad-hoc inference specification](../specs/ad-hoc-inference.md).
