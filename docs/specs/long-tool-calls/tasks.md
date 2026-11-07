# Long tool calls: tasks

## Group 1: Streamed replies

Requirements: R1, R2, R3, R7, R8, R9

Video: in the real app, a fake ACP agent calls Weavie tools over HTTP and through the stdio proxy, and a quick tool
answers while a slow one is still running.

- [x] Split `DispatchAsync` so the HTTP path parses a request once
- [x] Rework `HttpResponder` to own the stream and the one-answer rule, and remove `SendRawAsync` from the interface
- [x] Write each event on one `data:` line
- [x] Answer malformed bodies on the stream, keeping `202` for notifications
- [x] Remove the proxy's JSON branch and convert `McpStdioProxyTests` to event streams
- [x] Cover concurrent calls and an unchanged WebSocket path

## Group 2: Progress and hang-ups

Requirements: R4, R5, R6, R9

Video: in the real app, a slow fake consult shows progress reaching the caller; cancelling it stops the consulted
process; unloading the session gives the caller an error.

- [ ] Move the heartbeat into `HttpResponder` and delete `McpServer.Progress.cs`
- [ ] Cover progress values, token types, no progress after an answer, and no token
- [ ] Prove WebSocket calls with a progress token get no progress
- [ ] Treat a connection reset and a failed progress write as hang-ups
- [ ] Send `-32603` on server disposal

## Group 3: Real long consult

Requirements: R10, R11

Video: a real native-pane Claude session consults Astra past the 5-minute mark and receives the result.

- [ ] Correct `agent-consultation.md` and link this spec
- [ ] Run a consult longer than 5 minutes from a real native-pane Claude session
- [ ] Observe how real Claude treats a stream that ends with no reply, and record it
