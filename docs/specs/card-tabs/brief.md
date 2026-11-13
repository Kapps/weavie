# Card tabs — brief

**Status:** brief, revised after [challenge.md](challenge.md). Design follows.

## Goal

An agent can group charts and tables into a named dashboard, and a card in the agent pane opens in an editor
tab that shows its content at editor width and stays live. The motivating case: during a rollout the agent
keeps five signals current, and the user watches them in a tab instead of scrolling the chat back to them.

## Scope and decisions

- **The agent names a group.** A chart or table shown with a group joins that dashboard, whichever turn
  added it. A block without a group behaves as it does today.
- **A dashboard is a saved card with its own identity**, like a `/btw`: it has an id and a title in the
  conversation's history, and each member block's row names it.
- **The card sits in the chat** at the place its first block was shown, in the box `/btw` cards use today.
  It starts expanded with its blocks at full size and no left accent border on them, and collapses like a
  `/btw` card. Expanded, its header is the title and Open; collapsed, it adds the block counts and the last
  update time.
- **A block added to a dashboard later appears inside the card** and leaves one line at the turn that added
  it ("Error rate · added to Checkout rollout") that reveals the card.
- **Open puts a card's content in an editor tab of its session**, live in both places.
  - Dashboard: its blocks one per row at full width, in the order shown.
  - A block with no group: Open on its header gives a tab with that one block.
  - `/btw`: its transcript and its reply box.
  - Subagent and task: as today.
- **In the tab, each block's height can be dragged**, and the tab remembers it. Heights in the chat stay
  fixed.
- **A table's sort and filters are shared** by the chat card and the tab.
- **Selecting in a tab** attaches to the main composer, as selecting in the chat does today, including from
  a `/btw`.
- **The strip above the composer has two groups and no label.**
  - Running work first: subagents and tasks, with today's rules (Stop where it can stop, cleared at the next
    prompt once finished, no ✕). It still comes from the host's background-work store, which also decides
    whether stopping a session would lose work; dashboards and `/btw`s are not put into that store.
  - Then every expanded dashboard and `/btw`. Clicking an entry opens its tab.
- **✕ on a strip entry collapses its card.** Collapsed cards are not listed, so that removes the entry;
  expanding the card lists it again. There is no separate dismissal. Collapse is saved in the conversation's
  history, as a `/btw`'s is today.
- **The agent can open a card's tab when asked**, without taking focus unless the user asked for that.
- **Open and collapse act on a named card.** Neither picks "the most recent" one.
- **Subagent, task, and `/btw` cards keep their looks.**
- **Mobile:** Open switches to the editor view, as a subagent's Open does today.

## Edge cases

- Reload or restart: the card forms from the chat rows, before chart data arrives, so nothing shows loose and
  then jumps.
- `/clear`, or a Rewind past the content: the card, its collapse state, and its strip entry go with the
  history, and an open tab says the content no longer exists. A new dashboard with an old one's name is new.
- A Rewind that removes only some of a dashboard's blocks keeps the card with the rest; the line at the
  removed turn is what went.
- The same group in the main chat and in a `/btw` is two dashboards; a `/btw`'s dashboard sits inside that
  `/btw`'s card.
- A group with one block is still a card.
- A card that grows while the user reads above it or inside it must not move the transcript under them. This
  is the case to prove on a real iPhone.
- Card and tab showing at once is the normal case:
  - the reply box takes focus only where it was opened;
  - a selection drag inside a card keeps that card mounted until the drag ends;
  - the tab always shows the content expanded; collapse applies to the chat card.
- The `/btw` reply box in the tab and in the chat card hold the same draft, which is not kept across a
  reload, as today.
- A collapsed dashboard keeps updating, and an open tab stays open.
- A dashboard tab is visible only while its session is the selected one.

## Out of scope

- Any destination other than the editor tab: a pinned area in the agent pane, a dock pane, a separate window.
- A compact chat card with the tab as the only full-size view.
- Moving a block that is already shown into a group; the agent shows it again under the group.
- A grid, or any layout the agent chooses; reordering blocks; removing a block from a dashboard.
- Thumbnails or sparklines on a collapsed card.
- A second conversation inside a dashboard tab.
- A strip entry for a lone block, a dismissal separate from collapse, and a strip label.
- A marker for a block whose refreshes have stopped.
- Restyling subagent, task, or `/btw` cards.
- Terminal Claude, blocks from subagents, and Weavie refreshing a block itself.
- Phone-specific work.

## Open for the design stage

- What the agent passes to name a group (an id plus a title, or a title alone), and what the tool tells it
  back ("created dashboard X", "added to dashboard X").
- How the agent's "open" names its own conversation's dashboard without guessing.

## Approach

**What changes for the user.** Asking for a dashboard gives one card holding the charts, which stay full
size in the chat. Open on that card, on a `/btw`, or on a lone chart puts the same content in an editor tab
at full width. A strip above the composer, which never scrolls away, opens any expanded card again.

**The main pieces.**

1. *A saved dashboard.* The first block that names a group writes a dashboard row with an id and a title
   into the conversation's history, and each member block's row carries that id. Collapse is a row on that
   id.
2. *Three tabs behind one opener.* A conversation tab for a subagent or a `/btw` (the `/btw` adds its reply
   box), a blocks tab for a dashboard or a lone block, and the task tab as it is.
3. *The strip.* The web draws it from two sources: the host's background work, and the expanded cards in the
   transcript.

**Reasons.**

- A tab is a second view of the card's content and holds no copy of it, so the agent updates blocks exactly
  as it does today and both places redraw.
- A group the agent names survives "also add error rate" ten minutes later; grouping by turn would start a
  second dashboard.
- Chart data reaches the pane after the chat rows, and last of all on reload, so membership has to be in the
  rows. Saved in the history, a dashboard is also removed by `/clear` and Rewind with no second owner, and
  its tab, strip entry, and collapse all address one id.
- A block exists only while its row does, so a later block's row stays, and showing it as one line keeps
  Rewind and the new-output mark matching what is on screen.
- One state, collapsed, serves the chat and the strip, so nothing new needs a saved home.
- An editor tab, because watching and developing rarely share a session and each session has its own tabs,
  and because subagent tabs already work this way.
- Rows, because a chart's width is what the tab adds.
- A task is not a card (its tab reads the running-work list, not the transcript), so its tab stays apart.

**Constraint.** Each step is checked against real Claude and Codex agents as well as the fake one, including
that both pass the new optional group argument.

## Mockups

`temp/mockups/card-tab.png` and `temp/mockups/strip.png` (gitignored; copies in the workflow's shared
documents under `mockups/`) show the card, strip, and tab, taken from the real app with the real block
components. They predate the challenge: the strip there has a label, the expanded card's header shows counts
and a time, and the header buttons and ✕ are overlaid by hand, so alignment is not a reference.
