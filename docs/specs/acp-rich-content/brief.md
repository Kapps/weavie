# Rich content in the agent pane — brief

**Status:** brief, revised after [challenge.md](challenge.md). Design follows.

## Goal

An ACP agent can show charts, tables, and images in the native agent pane, keep a chart or table current
in place while it works, and the user can point at any part of what is shown and attach it to their next
prompt. The motivating case: during a rollout the agent charts live Datadog metrics against 24 hours ago,
refreshes every minute, and speaks only when something looks wrong, instead of printing the numbers again
each time.

## Scope and decisions

- **Weavie draws the content; the agent supplies data.** Charts and tables are described as data in a
  Weavie-owned format and rendered by Weavie, so they follow the theme and fonts, need no sandbox, and
  Weavie knows what every mark means.
- **Charts and tables come from a Weavie tool, and only from it.** The agent shows a block under an id and
  later updates that id, replacing its data or appending to it; the block changes in place. A malformed or oversized request is refused to the
  agent with the reason and the limit, so it corrects or aggregates. The call happens inside the reply, so
  the block sits in order between the surrounding text.
- **Charts** are drawn with Apache ECharts: line, area, bar, scatter; time axes, multiple series, a
  comparison series, threshold lines and shaded bands, plus whatever else the library makes easy. A new
  chart draws without an entrance animation; an update animates from the previous data.
- **Tables:** sortable and filterable. A log is a table with time, level, and message columns. Markdown
  tables stay as they are, for small static results.
- **Images** are ordinary markdown images, from the workspace or an https address. The host fetches a
  remote image and serves it to the pane, so the request comes from the machine the agent runs on and the
  picture can be cropped. A setting turns remote images off.
- **Blocks stay at their place in the transcript**, at pane width.
- **Attach what you point at.** Selecting a chart point or range, table rows, columns or cells, a boxed
  region of an image or diagram, or highlighted reply text or code adds it to the composer as an
  attachment, like a pasted image; several can stack, and the user describes what they want in the prompt
  text. A setting turns attach-on-highlight off, leaving a command. An attachment carries the values as they were when selected. Data selections reach the agent as
  text; an image region as the cropped picture.
- **Live updates are agent-driven.** The agent refreshes on its own schedule from any source it can reach.
  A block that has been updated shows its last update time and nothing else; Weavie never claims a block is
  live.
- **A block keeps only its latest state:** its id, kind, description (title, axes, series, thresholds or
  columns), current data, and last update time. An update overwrites the previous data.
- **Block lifetime follows its conversation.** A block survives restart and reconnect. `/clear` deletes the
  conversation's blocks; Rewind deletes those created after the cut and keeps earlier ones with their
  latest data; deleting a session or workspace deletes its blocks. Ids are scoped to the conversation, so
  one conversation cannot read or update another's blocks.
- **Weavie's own update call leaves no row;** it shows only while the call runs. Every other row behaves
  as it does today.
- **Conversations:** main chat, `/btw`, and workflow threads.
- **Mobile** renders every block; no touch-specific selection work.

## Edge cases

- The agent updates an id that does not exist in its conversation: the tool refuses.
- A block updates between selecting and sending: the attachment keeps the selected values.
- A dataset the agent sends is stored in the block, not in the transcript journal, unless the agent's
  permission mode shows an approval card for the call, which records its input as for any tool.
- An update's own call leaves a few small journal events without data.
- A block's row has a height known before its data arrives, and block data loads in one batch after
  history, so scrolling never shifts when data lands.
- Agents may call tools in parallel; each block lands once, on the row of the call that made it.
- An agent whose adapter does not relay tool results shows the plain tool row and no block.
- A remote image that fails to load, or exceeds the size bound, says so in the pane.
- An agent without image support refuses an image-region attachment the way it refuses a pasted image.

## Out of scope

- Raw HTML or scripted frames in the pane, and mockup cards linking to an HTML preview.
- Weavie-side polling, refresh commands, alert rules, and chart data read from a workspace file.
- Hiding or folding the agent's other refresh activity (fetch rows, wake-ups); revisited after this ships.
- A larger or zoomed view of a block; a later zoom feature for the agent pane covers it.
- Pinning blocks, a gallery for returning to earlier rich content, and an overdue marker for stale blocks.
- Terminal Claude (an editor-tab presentation may come later).
- Blocks from native subagents.
- A touch-optimized selection gesture, and keyboard picking of individual chart points or image regions.
- A note per attachment.

## Approach

**Phase 1, its own PR — make the conversation architecture ready.**

1. *A block tool call claims its own row.* An agent runs a Weavie tool by calling Weavie's tool server,
   and separately narrates the same call in its ACP stream; no id is shared by both for every agent. The
   tool server validates the request, holds it, and returns a random single-use token that it generated in
   the tool's result. The agent's adapter relays that result on the call's row, and the conversation that
   owns the row saves the block and turns the row into it (or, for an update, removes the row). Capture the
   real wire for Claude and Codex before designing further; the fake agent gains the same narration.
2. *A block store per conversation.* Blocks live beside the conversation's saved state, keyed by block id,
   overwritten on update and deleted with it or with its row. The transcript holds one fixed reference row
   per block, and an update publishes one small message to the pane.

**Phase 2 — the feature.** The show/update tool and its guidance to agents; chart and table rendering;
markdown images with the host fetch path and its setting; the selection
gestures and composer attachments.

Reasons: ECharts gives animated updates, a polished tooltip, range selection, and two y-axes without code
of ours, and has the larger community; its cost is re-applying theme colours on a theme change, as Mermaid
already does. Weavie-drawn blocks make feedback precise, the look native, and the pane safe, which raw HTML
cannot. A tool gives ids for in-place updates and returns errors to the agent. Agent-driven refresh works
with dynamic, MCP-backed sources. A token Weavie wrote needs no provider-specific id and no ordering
between the two channels. A store outside the append-only journal is the only place "latest state" can
live without growing history, and it gives Rewind and `/clear` a defined effect.
