# Background work in the native ACP pane

A native ACP session can run work beside its turns: **subagents** (child conversations the agent spawns) and
**background tasks** (a backgrounded shell command, a Claude workflow). Weavie renders both, lets the user stop
tasks, and refuses actions that would silently end them.

## Protocol

The native pane's `initialize` declares exactly:

```json
"clientCapabilities": { "subagents": {},
  "session": { "configOptions": { "boolean": {} }, "notices": {} },
  "_meta": { "jetbrains": { "air": { "version": 1, "capabilities": ["nativeSubagentSessions", "asyncTasks"] } } } }
```

Inference and consult clients declare none of these. The AIR extension is a documented, opt-in contract both
official adapters implement; declaring it also turns on the AIR base bundle, of which Weavie reads:

- `asyncTasks.backgrounded` on a tool call: the call handed its work to a task, so it never holds the turn
  (`AgentPaneMessage.Background` carries this to the pane);
- `subagent` on a tool call: its subagent's card represents it, so the row is not shown;
- `customAnswer` on an elicitation property: the free-text companion of a choice question, which therefore offers
  Other. A typed single choice is sent as the answer; typed text beside multiple picks goes into the companion.

AIR markers are sticky per tool call. Unknown update kinds still fail the session. claude-agent-acp hides a
subagent's spawning `Agent` tool call from the parent yet sends the parent a metadata-only update for it; an
update for an unannounced call that carries nothing but `_meta` changes nothing and is ignored, while any other
update for an unknown call remains a protocol failure.

## Ownership

```mermaid
flowchart LR
  Root["root AcpConversation<br/>(primary or /btw side)"] --> Work["AcpBackgroundWork"]
  Work --> Sub["subagent AcpConversation<br/>(role Subagent, AdoptedOpening)"]
  Sub -- nested spawn --> Work
  Work --> Tasks["async tasks"]
  Session["AcpAgentSession"] -- aggregates roots --> Items["BackgroundWork / agent/background"]
  Items --> Tray["tray, cards, rail status"]
```

- A `subagent_spawned` update adopts the child session as a read-only `AcpConversation`: its endpoint is opened
  and bound on the parent's process from inside the announcement, so none of the child's traffic precedes it.
  It has the full request machinery, so a subagent's approvals and questions are answered in its card through
  request ids namespaced by its conversation id. It never opens, prompts, steers, sets controls, or closes.
- Nested spawns join the same root's work, naming their parent; `subagent_state_update` ends a subagent (a
  `turn-completed` with its state and `CompletedAtMs`) and retires it once its own nested subagents have ended.
- Tasks are keyed by the agent's task id. Progress only updates the live item; a later terminal state corrects an
  earlier one (Claude reports `stopped`, then `completed`). A terminal state's `summary` (a failure's error) is the
  item's `Summary`; `toolCallId` and `outputFilePath`, which Claude may send only late, make the started tool call the
  item's transcript item and its output file the item's `OutputPath`. Stopping a task sends `_session/async_task/stop` on
  the root conversation's endpoint even when a subagent started it.
- A conversation loading history treats announcements as replays: a replayed id gets a retired sink owner whose
  traffic drops, an id already owned keeps its owner, and their later states are ignored. A retired endpoint that
  receives an announcement sinks the child the same way.
- Finished items stay until the root's next prompt; there are no timers.

## Lifecycle

```mermaid
stateDiagram-v2
    [*] --> Running: spawned
    Running --> Paused: agent pauses (task)
    Paused --> Running
    Running --> Completed
    Running --> Failed: agent reports, or the root fails
    Running --> Stopped: user Stop, restart, /clear, Rewind, unload
    Stopped --> Completed: a later terminal state corrects it
    Completed --> [*]: cleared at the next prompt
    Failed --> [*]
    Stopped --> [*]
```

The root's failure ends its subagents and tasks as failed; a restart, `/clear`, or Rewind ends them as stopped.
Running work keeps the session status on Waiting unless it works or needs input, which holds the update drain.

## Presentation

- **Subagent card** — a transcript row where it was spawned: label, live state and elapsed time, the parent of a
  nested spawn, its latest activity and any request. Expanded while running, collapsed when finished; Open
  (`weavie.agent.openSubagent`) shows its whole transcript as a read-only editor tab of its session, rendered
  with the agent pane's own transcript components and updating while the subagent runs.
- **Workflow card** — a Claude workflow (`taskType` `workflow`; Claude reports `showInTranscript: false` for
  workflows) journals an item `task:<id>` from spawn to its final state, joined live with the task's usage and
  Stop. Phase 2 (phases, agent rows, log lines) attaches to this item.
- **Task tab** — a task's own read-only editor tab (path = its item id): type, live state and elapsed time, start
  time, usage, detail, last activity, result or error, a link to its output file, and the output of the tool call
  that started it. It follows the task live and says so once the task is cleared.
- **Tray** — a roving-focus toolbar above the composer lists every item; clicking or Enter opens the item in a tab
  (a subagent's transcript tab, else its task tab), and Stop appears only for stoppable tasks.
- **Commands** — `weavie.agent.stopBackgroundTask` (Core, `{id?}`; no id stops the most recently started
  stoppable task) and `weavie.agent.showBackgroundWork` (Web, focuses the tray), both gated by
  `agentBackgroundActive`; `weavie.agent.openBackgroundItem` (Web, `{id?}`; no id opens the most recently started
  item, and an unlisted id is refused) is ungated, since finished items stay openable. None has a default keybinding.

## Stopping actions

Unload, delete, Recreate, Restart Agent, `/clear`, and Rewind refuse while background work runs, with
`{"session":{slot,incarnation},"backgroundWork":[{name,type,state,startedAtMs}]}` as the result data, unless called with
`stopBackgroundWork: true` (MCP callers see the same refusal). The web
turns it into a "Stop background work?" confirm whose list follows that session's work live. Its confirm names the
action ("Stop and restart", "Stop and unload", …) and re-runs with consent; Keep running is the Enter/Esc default. Work
that finishes while it is open, or whose session goes away, re-runs the action without consent, so anything started
since is asked about again.
Exit Weavie asks about every loaded session on this machine.
A page reload or Reload Agent History does not ask: the work keeps running on the host. With running work,
`/clear` and Rewind restart the process instead of replacing the conversation on it, since only that stops it.
