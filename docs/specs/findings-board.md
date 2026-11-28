# Findings board

An agent's long turn ends in one long reply: everything it learned arrives at once, as a wall, after a wait with
nothing to read. The findings board gives the agent a place to put each thing it learns when it learns it, in a
form that costs one sentence to read.

## What the user sees

- **A finding** is a one-sentence title that is always visible, over evidence (markdown) that stays closed until
  the user opens it.
- **The board** lists the conversation's findings above the composer, in the order they were pinned. It appears
  with the first finding, stays through the reply, and rests at its newest row. `Expand or Collapse Findings`
  (`weavie.agent.toggleFindings`, unbound) folds it to its header and count.
- **In the reply**, a line that is only `[[id]]` shows that finding at that place, as the same row. The board's
  row and the reply's open independently. A line naming no finding stays as the text the agent wrote.
- **A correction** is the same id pinned again. The finding changes in place, on the board and in every reply
  that placed it, and shows when it was updated.

A finding is never a transcript row of its own: not as a tool call, and not as a block.

## What the agent is told

Weavie describes the tool and adds the user's own ask to their prompt; it never tells an agent how to write
through a system prompt.

- **The tool.** `pinFinding(id, title, detail?)` is listed with the block tools, to a conversation that can show
  them. Its result names the finding and tells the agent to place it as `[[id]]` and not restate it. Nothing
  limits a finding's length.
- **The ask.** With `agent.findingsBoard` on (the default), each prompt ends with
  "Pin what you find to the board as you go. I read it while I wait, so don't repeat a pin in your reply; refer
  to it." Without it agents do not call the tool.
- **The reader.** With `agent.adhdReader` on (off by default), each prompt also ends with "I have ADHD. I lose
  the thread in long paragraphs and dense wording, so keep what you write to me brief and plain. Brief is about
  the words: do the work as thoroughly as you otherwise would, and still tell me the caveats and gaps." The
  second sentence is there because without it agents did less work and dropped caveats.

The sentences are one extra text block after the user's text, in the user's voice. The transcript, prompt
history, Rewind and `readSession` show only what the user typed.

| Prompt | Pin ask | Reader |
|---|---|---|
| Composer prompt, steer, bundled prompt, workflow prompt stage, review hints | yes | yes |
| `/btw` side conversation, which has no board | no | yes |
| A message from another session | no | no |
| A provider slash command, which must stay an exact block | no | no |
| Terminal Claude, consultations, ad-hoc inference | no | no |

## How it works

A finding is a third kind of [agent block](acp-rich-content/design.md), so it inherits the block machinery:
the claim token on the tool's result, latest-state storage, the `block` push, history, Rewind and `/clear`.

```mermaid
sequenceDiagram
    participant A as Agent
    participant M as McpServer
    participant C as AcpConversation
    participant P as Web pane
    A->>M: pinFinding(id, title, detail)
    M-->>A: token + "Finding 'id' is pinned… write [[id]]…"
    A-->>C: tool row carrying the result
    C->>C: claim the token: first pin or re-pin?
    C-->>P: first pin: a block row of category "finding", and the block
    C-->>P: re-pin: the tool row retracted, and the block's new state
    P->>P: the row joins the board; nothing is drawn at its place
    A-->>C: reply text with a line "[[id]]"
    P->>P: the line becomes a slot, shown as the finding's row
```

- **Core.** `AgentBlock.Finding` holds the detail and the block's title is the finding. `AgentBlockRules.Pin`
  validates a pin and `Apply` replaces a finding with its next pin, stamping `UpdatedAtMs`. A finding and a chart
  or table never share an id, and `updateBlock` refuses a finding.
- **Claim.** The first pin of an id turns its tool row into the block row; a later pin retracts its own row, as
  an update does. A `pinFinding` row is never drawn while in flight.
- **Prompt.** `AgentPromptPreferences.Compose` returns the enabled sentences and `BuildPrompt` adds them.
- **Web.** The projection collects `finding` rows into the snapshot's `findings` and adds nothing to the
  transcript. `AgentFindings` renders the board from them and `AgentFindingRow` renders one finding from the
  block store. A markdown-it rule turns `[[id]]` lines into slots before inline parsing, and `AgentMarkdown`
  mounts a row in each slot of a reply.

A subagent's pin is discarded with a notice, as its blocks are. A side conversation's findings are its own and
show only where its replies place them.

## Evidence

Measured with real agent runs on one research task and one fix task (one or two runs per arm, so direction is
firmer than size). "Visible words" are what the user must read: titles plus reply.

| Arm, Opus | Visible words | Commands run |
|---|---|---|
| No board | 853, 1,038 | 47, 39 |
| Board | 643 | 34 |
| Board, result reminds the agent to place and not restate | 561 | 33 |
| Board and reader sentence | 541 | 40 |

- The tool description alone produced no pins in six runs; the ask produced them in every run.
- A 60-word limit on a finding caused refusals at 61 to 64 words, so there is none.
- The reader sentence without its second sentence cut the commands run to 26 and dropped the gaps from a fix
  report.
- Three or four of ten findings land in the last quarter of a turn, so reading during the wait is a smaller gain
  than the shorter reply.
- No run corrected a finding, so replacement is untested with real agents.
