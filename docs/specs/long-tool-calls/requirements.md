# Long tool calls: requirements

Requirements use EARS wording. "Caller" is the agent calling a Weavie tool over HTTP MCP.

## R1. The reply starts at once

As a caller, I get a response immediately, so my header timeout never fires during a long tool.

- WHEN an authorized HTTP MCP request arrives, Weavie SHALL send a `200` response with `Content-Type:
  text/event-stream` before dispatching it.
- WHILE a tool is running, the caller SHALL already hold the response headers.

## R2. Every reply is an event stream

As a client author, I handle one reply format.

- WHEN Weavie answers a request, it SHALL deliver the JSON-RPC message as an SSE event and then close the connection.
- Each event's JSON SHALL occupy one `data:` line, even when the source JSON contains newlines.
- A call SHALL produce at most one answer.
- A malformed body SHALL get a `-32700` error event.

## R3. Notifications stay empty

- WHEN a JSON-RPC notification arrives, Weavie SHALL answer `202 Accepted` with no body.

## R4. Running calls report progress

As a caller with an inactivity limit, I keep waiting while a tool runs.

- WHILE a `tools/call` carrying a progress token runs, Weavie SHALL send `notifications/progress` on that token every
  30 seconds, with an increasing `progress` value.
- WHEN a call has been answered, Weavie SHALL send no further progress for it.
- IF a request has no progress token, Weavie SHALL stream the reply without progress.
- Only `tools/call` SHALL report progress.

## R5. A caller that leaves stops its call

As a user, I don't pay for a consult nobody can receive.

- WHEN the caller sends `notifications/cancelled`, Weavie SHALL cancel the matching call and send no answer.
- WHEN the caller closes or resets the connection, Weavie SHALL cancel the call.
- WHEN a progress write fails, Weavie SHALL cancel the call.

## R7. Weavie's own clients read the stream

- The stdio proxy SHALL relay each event as one stdout line and SHALL have no plain JSON path.
- `Weavie.FakeAcp` SHALL read a tool's result from the event stream.

## R8. Concurrent calls stay independent

- WHILE a call streams, Weavie SHALL accept and answer other requests on their own connections.
- A cancellation SHALL affect only the matching call of the same MCP session.

## R9. Terminal Claude is unchanged

- The WebSocket transport SHALL behave as it did, with no progress messages.

## R10. Limits are documented

- `docs/specs/agent-consultation.md` SHALL state the measured Claude and Codex limits and that a disconnect cancels a
  call on purpose.

## R11. A long consult works for real

As a user, I can ask Astra for a review that takes many minutes.

- WHEN a consult runs longer than 5 minutes from a real native-pane Claude session, the session SHALL receive its
  result.
- WHEN a call ends with no reply, as a cancelled call does, real Claude's behavior SHALL be observed and recorded.
