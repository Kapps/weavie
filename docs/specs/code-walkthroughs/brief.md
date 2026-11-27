# Code walkthroughs

## Goal

Help the user understand unfamiliar code beyond the diff: the theory behind it (what the parts are, the decisions
that shape them, why, and what follows), not a call path. Most useful while an agent works: once it understands the
code it is about to change, it publishes how that code works today; after its edits, the same walkthrough shows
which parts of the explanation the change alters, including effects in code it did not touch.

The form was settled through interactive mockups. The first form, a list of claims each proven by short snippets,
did not help: it asked the reader to trust conclusions and never let them read the code. The form below is the
second mockup, `temp/walkthrough/reading/index.html` (not committed). An independent challenge of earlier versions
of this brief is in [challenge.md](challenge.md); its findings on anchors, copies of the code and the Ask's limits
still apply.

## What the user sees

- **A walkthrough is a guided read**, opened from a card in the agent pane into an editor tab. It is one centred
  column, about 900px wide, with nothing beside it.
- **It starts from the problem**: a few sentences on what the code has to solve, the question that raises, and
  "the short version", one paragraph that answers it before any code.
- **Then stops, in the order the code runs.** Each stop has a title, a short lead that says what to read for, and
  whole functions or types from the real files, not snippets. Parts that are not the point are folded behind a
  labelled row and open on click.
- **Notes sit under the lines they describe**, with the token they are about marked.
- **One concrete example is followed through.** Under a stop's code, an "After this stop" box says what the example
  looks like at that point (what is held, saved, on screen). These values are the agent's account, drawn as prose
  and never as code.
- **Why it is built this way** sits beside the code that shows it, with its source cited (spec, PR, commit, or
  marked as the agent's own reading). A stop can also ask the reader to predict something, with the answer hidden
  until asked for.
- **Diagrams are ordinary content.** The agent places one where a picture explains something. There is no
  permanent map: a diagram has to be decoded, so it is not something to keep in view.
- **A strip along the bottom of the tab** lists the stops by name: the current one lit, the ones already read
  marked, and previous and next. Clicking a name jumps to it. The floating review bar does not show over a
  walkthrough.
- **Code blocks are read-only editors** on the real file: hover and go to definition work as in the review, with no
  Keep or Revert. The diff runs from the code as the walkthrough first recorded that file to the file now.
- **After the agent's edits**, the walkthrough is updated, not replaced. A box at the top says what the agent
  changed and lists the stops it touched. A stop is tagged *changed* (its explanation changed, with what it was),
  *plays out differently* (its code is untouched but behaves differently now) or *new*. Changed code shows removed
  and added lines, with notes rewritten on the new lines. The strip marks the same stops.
- **Asking while reading**: selecting any text, prose or code, offers Weavie's selection actions. A comment is
  answered in a card under what was selected, with the selected text kept lit, a Reply box and Stop while it works.
  The working agent is not interrupted.

## Decisions

1. **Native ACP agents only, desktop first.** Terminal Claude cannot show cards; phone layout is not designed for.
2. **When the before is published**: a setting, off by default, adds guidance telling the agent to publish a
   walkthrough once it understands the code it is about to change, and before editing it. Understanding is the
   trigger; the first edit is the deadline. Turning it on applies from the next conversation. The user can also ask
   for a walkthrough at any time, with or without a change in progress.
3. **The agent writes walkthroughs with dedicated Weavie tools** (publish, update). They reuse the agent-blocks
   machinery for the journaled row, the claim token, the card and the tab; the walkthrough itself is a new kind of
   block with its own limits.
4. **Weavie checks every anchor** before accepting it: the file is inside the worktree and is text, the lines exist,
   each fold lies inside its block, and each note's token appears on its line, all against the saved file. A failed
   check is refused to the agent with the reason and the real text, so it can correct itself. Code recorded from a
   file the session's agent has already edited is accepted and labelled "recorded after edits to this file began".
5. **The agent sends anchors, never code.** A code block is a file and a line range; Weavie reads the lines itself.
   Prose is markdown, so a diagram is a Mermaid block in it.
6. **Only code is checked.** Everything else (leads, notes, the example's state, why, predictions) is the agent's
   writing and is drawn as prose. Nothing the agent states is drawn as if it were code.
7. **The walkthrough keeps its own copies of the code.** When a block is recorded, Weavie stores the cited file's
   text once by content hash beside the walkthrough, as review hints do, and deletes it with the walkthrough. Each
   block places its notes and folds against the copy it was recorded on, mapped across to the file now. Every block
   on a file diffs from the first copy the walkthrough recorded of it. An update records each cited file as it
   stands; a block whose file has moved on since says "changed since this walkthrough was updated". Nothing depends
   on the review's state.
8. **Notes sit on lines that exist.** When an update alters a stop, the agent records that stop's code again against
   the edited files; removed lines show red and carry no note. A note whose line changed without such an update is
   not drawn, and its block says a noted line changed.
9. **Updates change one stop at a time** (add a stop, or change one with its code recorded again) and apply in
   arrival order. The agent names the walkthrough it updates. An unknown walkthrough or a stop that cannot be
   checked is refused with the reason.
10. **A question is a `/btw` asked from a selection.** It forks the working session, knows what the working agent
    knows, and answers while that agent works, with the trust of a `/btw`: told not to edit files, running with the
    working agent's permissions and model. It differs from a `/btw` in one way: its card is drawn under the
    selection in the walkthrough and not in the pane's transcript. A failure or a pending approval shows on that
    card. Reply continues it. Rewind, `/clear` and restart treat it as they treat a `/btw`.

## Edge cases

- A cited file changed after the last update: the block shows the diff and says the file changed since.
- A cited file deleted since: the block says so and shows the recorded copy.
- A file too large to diff: the block shows the recorded lines without a diff and says so.
- A refused tool call (bad line, missing token, unknown file, a fold outside its block): the agent gets the reason
  and retries; nothing partial is shown.
- A question whose stop or line no longer exists after an update: its card moves to the end of the stop, or of the
  walkthrough, and says the text it was asked about has changed.
- A question that fails, or whose agent restarts: the card says so; it never disappears silently.
- Rewind to before the editing turn keeps the walkthrough's latest state, as for every block.

## Verified during design

- A fork that lands inside a tool call opens at once on Claude and Codex, knows what its parent knew, and leaves
  the parent's turn untouched ([design.md](design.md), Fork test). A question never waits for the working turn.

## Out of scope

- Terminal Claude, non-ACP backends, and phone polish.
- Running code to verify a claim or to capture the example's real values.
- A permanent map of the walkthrough, and answers that add stops to it.
- Familiarity-based folding.
- Sharing a walkthrough outside its session, e.g. attaching it to a PR.
- Following cited code across commits.
- A question on a cheaper model or a restricted, read-only process.
- The Steer vs Run gap found while tracing (the composer labels its button Steer whenever a turn runs, while the host
  may queue the prompt instead); it is filed as #1297.

## Approach

Walkthroughs are a new kind of agent block. The agent produces the content (the problem, the stops, and for each
stop its prose and its code as file and line anchors with folds and notes) through Weavie tools; Weavie validates
the anchors, keeps a copy of every cited file, saves the walkthrough with the conversation, and draws everything:
the card, the reading tab, the bottom strip, and the code blocks as read-only diff editors from the first recorded
copy to now. This keeps the agent responsible for the explanation and Weavie responsible for presentation and for
guaranteeing every block is real code. Questions reuse two things Weavie already has, the selection actions and
`/btw`, so asking from a walkthrough gets the working session's context without derailing it and needs no new kind
of conversation.
