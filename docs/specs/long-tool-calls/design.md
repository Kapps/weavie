# Long tool calls: design

The [overview](overview.md) has the flow and pseudo implementations. This file holds the decisions they leave open.

## Interfaces

| Member | Change |
|---|---|
| `IMcpResponder` | Loses `SendRawAsync`; keeps `Client`, `SendResultAsync`, `SendErrorAsync` |
| `McpServer.ResultJson`, `ErrorJson`, `ProgressJson` | Private static envelope builders |
| `McpServer.ProgressInterval` | Internal `TimeSpan`, 30 seconds, shortened by tests |
| `McpServer.DispatchAsync(responder, root, ct)` | New overload taking a parsed root; the string overload parses and calls it |
| `HttpResponder` | `Opened`, `Progress`, `OpenAsync`, `StartProgress`, `ExpectNoReply` |
| `WebSocketHandshake.WriteEventStreamHeadAsync` | Replaces `WriteJsonAsync` |

`ProgressInterval` is not a setting: no user decision depends on it, and it only has to stay well inside Claude's
5-minute limit.

## Data

- **Progress token:** kept as raw JSON text, because MCP allows a string or a number and the reply must echo it exactly.
- **Progress value:** a counter from 1. There is no `total`, because Weavie can't know a tool's length.
- **Session id:** a client without one is issued one on the reply head, as today. It scopes cancellation.
- **State:** `HttpResponder` lives for one POST. Nothing about a stream outlives its connection, and no event ids are
  issued, so a client can't resume.

## Reply rules

| Situation | Reply |
|---|---|
| Request | `200` event stream, opened before dispatch |
| Notification | `202`, no body |
| Malformed JSON | `200` event stream with a `-32700` event |
| Bad token | `401`, as today |
| Wrong method or path | `404`, as today |

Any future rejection by status code must run before the stream opens.

## Error handling

| Event | Behavior |
|---|---|
| Tool throws | Dispatch sends a `-32603` event, which counts as the answer |
| Tool stops itself with a cancellation | Dispatch sends a `-32603` event, because real Claude doesn't notice a closed stream |
| Caller cancels | Call token cancels; stream closes with no answer |
| Connection closes or resets | `CancelOnDisconnectAsync` cancels the connection token |
| Progress write fails | Heartbeat cancels the connection token |
| Result write fails | The `IOException` is treated as a hang-up and ends the handler |
| Server disposed | Running calls cancel and their streams close |

Every write goes through the gate, so a late heartbeat tick sees `_answered` and writes nothing. A session's MCP server
is disposed only after that session's agent process has exited, so no caller remains to hear a shutdown error.

A failed progress write can't be tested on its own: the disconnect watcher sees the same dropped connection first.
The heartbeat still handles it, because the write can lose that race.

## Concurrency

Each accepted connection already runs its own `HandleClientAsync`. `HttpResponder`'s gate orders only its own writes,
so calls share no write path. `CancelOnDisconnectAsync` reads while writes happen, which `NetworkStream` allows.

## Testing

Tests are deterministic and shorten `ProgressInterval`.

- **Core (`McpAgentToolsTests`, `McpServerTests`):** head arrives while a tool is blocked; progress values and token
  echo for string and number tokens; no progress after the result or without a token; notification `202`; malformed
  body; multi-line JSON stays on one `data:` line; cancel, close, and reset each cancel the
  call; a quick call completes while another streams; WebSocket calls get no progress.
- **Hosting (`McpStdioProxyTests`):** stubs reply with event streams; `tools/list` relays as one line.
- **Real clients:** a real native-pane Claude session consults for more than 5 minutes. A real cancel shows how Claude
  treats a stream that ends with no reply.
