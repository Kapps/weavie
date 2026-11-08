# Long tool calls

## Goal

An agent can consult another agent for a real code review ("check with Astra") and get the answer, even when the
review takes many minutes. Today a consult from a native-pane Claude session always fails after about a minute.

## Background

Observed with real clients and a stand-in tool server:

| Caller | Limit | Old reply (one body at the end) | Streamed reply with progress |
|---|---|---|---|
| Claude Code over HTTP MCP | Response headers within 60s; then 5 minutes without progress | 75s call failed at 69s: "The operation timed out." | 400s call succeeded |
| Codex (`codex exec`) | 300s per call; progress does not extend it | not tested | 90s succeeded; 400s failed at 300s |

Weavie's HTTP MCP endpoint sends nothing until a tool finishes, so the first Claude limit always fires, and the
caller hanging up cancels the consult.

## Users

Anyone working in a native-pane agent session who asks it to consult another agent. The primary case is Claude
asking Codex's Astra model for a code review.

## Scope

- Weavie's HTTP MCP endpoint starts its reply as soon as a request arrives and delivers the result when it is ready.
- A running tool call reports progress often enough that an inactivity limit never fires.
- This applies to every Weavie tool call, not only `consultAgent`.
- Weavie's stdio MCP proxy and test fakes handle the same reply format.
- `docs/specs/agent-consultation.md` states the real caller limits.

## Decisions

- The calling agent waits for the consult. There is no background mode.
- Weavie adds no time limit of its own.
- The result returns as text. The calling agent adds any findings with the existing `addReviewHint` tool.
- A Codex caller's limit of about 5 minutes is accepted for now. It reaches the agent as a timeout error.

## Edge cases

- The caller cancels or hangs up: the consult stops, as it does today.
- A caller sends no progress token: the reply still streams, without progress messages.
- No progress message follows a call's result.
- A notification, which has no reply, is still answered 202.

## Out of scope

- Background consults with a separate call to collect the result.
- A live status or card showing the consulted agent's work.
- New consult options such as reasoning effort, and multi-turn consults.
- Raising or configuring a calling agent's own limit.
- Changing the terminal Claude connection, which has neither Claude limit.

## Done when

A consult that runs longer than 5 minutes completes in a real native-pane Claude session and returns its result.
