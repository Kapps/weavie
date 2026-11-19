# Workflow insights

## R1. Inspect one run

As someone running a workflow, I want to open its insights during the run and after it ends so I can investigate the same run at either point.

1. WHEN I open insights from a workflow run, Weavie SHALL identify the workflow, the selected run and its current or final state.
2. WHILE the run is live, Weavie SHALL update its current stage, visit counts, elapsed time and reported token usage without requiring a refresh or an agent prompt.
3. WHEN I return to a session with a saved completed or cancelled run, Weavie SHALL let me select that run and inspect its recorded visits, including an interrupted final visit, even if a newer run has started.
4. WHERE insights can be opened with the mouse, Weavie SHALL also provide a palette command and advertise its effective shortcut when bound.

## R2. See which stages consumed the run

As a workflow author, I want visits, tokens and time together for each stage so I can decide which stage to investigate.

1. WHEN I inspect a run, Weavie SHALL list the stages from the definition that run executed, in workflow order and with their nesting, including stages the run never reached.
2. WHEN I view the stage list, Weavie SHALL show each stage's visit count, recorded tokens and elapsed time, with the time breakdown defined in R4.
3. WHEN routing arrives at a stage, Weavie SHALL count one visit even if its entry condition skips the body. A branch that bypasses that stage entirely SHALL leave its count unchanged.
4. WHEN a loop, retry or manual jump enters a stage again, Weavie SHALL count a new visit. Further replies, completion rejections, and reconnects within an existing visit SHALL preserve its count.
5. WHERE a stage repeats over a list, Weavie SHALL count one visit to that stage per traversal and show the individual item executions within it. Reaching it once with an empty list SHALL show one visit and zero items. Reaching it once with three items SHALL show one visit and three item executions. A nested review stage reached once per item SHALL show three review visits, each identified by item.

## R3. Trace the back-and-forth

As someone investigating a repeated stage, I want to see each visit in context so I can tell planned repetition from rework.

1. WHEN I open a stage, Weavie SHALL list all its visits in execution order and show each visit's position in the run, outcome or current state, reported tokens, working time and waiting time.
2. WHEN a visit starts, Weavie SHALL identify what brought the run there, such as the start of the run, the preceding stage, a decision and its selected answer, the next list item, a retry or a manual jump.
3. WHEN a completion is rejected within a visit, Weavie SHALL show the rejection and its recorded reason on that visit.
4. WHEN a review decision sends the run through `implement → review → implement`, Weavie SHALL show two implement visits and one review visit. The second implement visit SHALL identify review and the answer that sent the run back.
5. WHEN a review takes four agent turns within one visit, Weavie SHALL show one visit with four turns, so the reviewer can see the exchange that a visit count alone would hide.

## R4. Distinguish work from waiting

As someone investigating a slow stage, I want to tell agent work from time the workflow spent waiting for me.

1. WHILE a visit is open, Weavie SHALL measure wall-clock time from entry through its entry condition, body and outgoing decision as applicable. WHEN the visit ends, its elapsed time SHALL stop advancing.
2. WHILE the run reports agent work or a model decision, Weavie SHALL attribute the interval to working time. WHILE it reports waiting for a reply, a user decision, background work, a pause or a stall, Weavie SHALL attribute the interval to waiting time and identify the reported state.
3. IF an interval has no observed run state, Weavie SHALL label its duration unclassified. Working, waiting and unclassified intervals SHALL account for the visit's elapsed time exactly once.
4. WHEN a visit works for 30 seconds, waits for approval for 90 seconds and works for another 10 seconds, Weavie SHALL show 40 seconds working, 90 seconds waiting and 130 seconds elapsed.
5. WHERE a containing stage displays time including nested work, Weavie SHALL identify that scope. Run elapsed time SHALL measure the run's own wall-clock interval so that nesting does not multiply it.
6. WHILE a visit is open, Weavie SHALL show its current state, how long it has been in that state, and the recorded question, pause reason or other blocker when available.

## R5. Trust the token counts

As someone investigating token use, I want reported usage attributed to the visit that incurred it so I can spot expensive steps even when they share an agent conversation.

1. WHEN a provider reports tokens for work belonging to a visit, Weavie SHALL attribute that usage to the originating visit even if the report arrives after the run advances. A stage's entry and outgoing decisions SHALL belong to that stage's visit, including a decision taken once for an entire list.
2. WHEN Weavie shows stage or run totals, it SHALL count each attributed token once, including across repeated reports and nested stages. Activity from unrelated work in the same session SHALL be excluded.
3. IF some work has no attributable usage report, Weavie SHALL mark the known subtotal as partial. IF no usage is known for work performed, Weavie SHALL show usage as unavailable. A reported zero SHALL remain distinguishable from either case.
4. WHEN two visits report 800 and 200 tokens, Weavie SHALL show 1,000 tokens for the stage. IF a third visit performs work with no usage report, Weavie SHALL show 1,000 known tokens with partial coverage.
5. WHEN I open or revisit insights, Weavie SHALL display the recorded evidence without invoking a model to reconstruct the route, estimate tokens or explain the results.
6. WHERE the provider supplies a total for an operation, Weavie SHALL use that total. WHERE the provider supplies token components, Weavie SHALL count non-overlapping input and output tokens once, including any cached or reasoning tokens already included in those counts. A context-window reading alone SHALL leave usage unavailable.

## R6. Trace a long review or heavy model usage to its source

As someone whose Fable allowance ran out during a day of reviews, I want to see which workflow work used that model and what kept a review open.

1. WHEN I inspect a review conversation or session linked to a workflow visit, Weavie SHALL identify its owning run and stage and let me open the corresponding visit. IF there is no recorded workflow association, Weavie SHALL say so.
2. WHEN I inspect a run's token usage, Weavie SHALL show the recorded tokens for each provider and model and the contributing stages and visits. IF the model is unknown, Weavie SHALL identify that usage as unattributed to a model.
3. WHEN a review stage uses 1,200 reported Fable tokens and its routing decision uses 80 reported tokens on another model, inspecting Fable's usage in this run SHALL identify the review visit and show 1,200 tokens. The stage total SHALL show 1,280 tokens.
4. IF a provider reports a usage limit as the reason a review stopped, Weavie SHALL show that reason on the affected visit. Run-scoped token totals SHALL be labeled with their scope so a user can distinguish this run's contribution from account-wide usage.
