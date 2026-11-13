# Command picker learning

The command palette and slash menu rank eligible commands using text relevance and workspace-owned
usage history. Ranking is deterministic and runs in the client on each keystroke; it uses no model.

## Research

- [VS Code's command palette](https://code.visualstudio.com/updates/v1_14#_command-palette)
  persists recently used commands between restarts and allows history to be cleared or disabled.
- [Raycast's search ranking](https://manual.raycast.com/search-bar) combines query relevance with
  frequency and recency. Aliases and title matches have priority over metadata matches, and users can
  reset learned rankings.
- [Alfred's result ordering](https://www.alfredapp.com/help/kb/understanding-result-ordering/)
  combines rolling usage knowledge with associations between an exact query and the selected result.
  This lets an abbreviation favor a less commonly used action. Learning can be reset or disabled.

Weavie uses these patterns to make repeated actions faster while preserving explicit typed intent.

## Ranking

Candidates come only from the current command catalog and eligibility checks. History cannot make an
unavailable command appear. Both pickers use the same ranking function:

1. Exact title/name or alias.
2. Title/name or alias prefix.
3. Fuzzy title/name or alias match.
4. Word-fragment matches across title and metadata (palette category or slash description).
   Description prose does not participate in fuzzy matching.

Within a tier, fuzzy score receives bounded boosts for usage and choices for the normalized query.
Query learning has the stronger boost, and both fade as their decayed weights decrease. Empty input ranks by decayed usage. Ties sort by title, then identity. Title highlighting
uses title-match positions, so matching metadata does not highlight unrelated title characters.
Slash results remain scrollable and keyboard-reachable, including those beyond the initial viewport.

Each recorded use contributes one unit of weight with a seven-day half-life. At the next use, existing
weight decays before the new unit is added. Reusing an old favorite does not revive its lifetime count.
Exact-query signals use the same decay. These weights are Weavie's policy, not a reproduction of another
product's proprietary algorithm.

## Ownership and capture

`CommandUsageStore` persists under the workspace state directory in `command-usage.json` using atomic,
source-generated JSON. HostCore owns the store and publishes snapshots to connected clients. Each
client keeps history keyed by backend; the connection handshake supplies its initial history and
reconnect replaces it without a separate request. Filesystem failures surface as failures,
not successful learning updates.

Weavie commands share their command id across palette and slash entry points. Provider commands use
provider id plus command name; bundled prompts use their prompt name in a separate namespace. No
arguments, prompt bodies, attachments, or file contents are recorded. Only command identities, picker
queries, weights, and timestamps are stored.

The palette records successful, non-cancelled command dispatches. Slash completion remembers the
selected query but does not learn merely from filling the draft. Weavie slash actions record successful
dispatches; provider commands and prompts record accepted submission receipts exactly once. `/btw`
records its accepted outer action separately from any inner provider command. Typed exact commands
also count. Shortcuts, model-invoked commands, and internal dispatches do not train these pickers.

## Controls

`commands.learning` defaults to `true`. Disabling it stops recording and ignores retained history in
both pickers. Each choice captures the preference before dispatch, so re-enabling cannot record an
action selected while learning was disabled. Re-enabling resumes learning. **Reset Command Learning** clears this workspace's history,
including query associations; it is a host-scoped command with no default keybinding and works even
when no session is loaded.

## Validation

Core tests cover decay, persistence, normalization, reset, and explicit failure on corrupt history.
Web tests cover relevance tiers, query learning, stable highlights, availability, and accepted versus
rejected or duplicate receipts. Full-stack tests cover palette and slash interaction, actual host
restart, browser reconnect, reset, and live disabling/re-enabling. Render-budget tests guard the
per-keystroke path.
