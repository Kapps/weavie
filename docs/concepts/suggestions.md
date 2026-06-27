# Contextual suggestions

A Core-owned surface for **dismissible, contextual nudges** that teach the user what Weavie can do.
A suggestion is *declared* once (`SuggestionDefinition`, in `CoreSuggestions`), *evaluated* against the
current workspace by the per-workspace `SuggestionService`, and *rendered* as a small card the user can
act on or dismiss. The first instance offers to configure `worktree.setupCommand` when the repo carries a
dependency/build manifest but the setting is empty.

The pieces mirror the command + settings systems:

- **`SuggestionRegistry`** — the catalog of built-in `SuggestionDefinition`s, threaded through
  `HostServices` like `CommandRegistry`. A definition carries a pure `IsRelevant` predicate and a list of
  `SuggestionAction`s (RunCommand / Snooze / DismissForever).
- **`SuggestionService`** (per workspace) — evaluates the catalog, pushes the active set over the bridge
  (`suggestions`, ambient/fan-out like `session-list`), and owns the in-memory snooze set plus the
  bounded, memoized, **fail-open** manifest probe (off the hot path).
- **`SuggestionDismissals`** — per-workspace JSON store (`~/.weavie/workspaces/<id>/suggestions.json`)
  persisting only the durable "don't ask again" ids; snooze is in-memory and clears on restart.

Two invariants worth keeping:

- **A nudge never spends model tokens until the user clicks.** "Yes" on the worktree card runs the Core
  command `weavie.worktree.suggestSetupCommand`, which **pre-fills** (does not submit) an analysis prompt
  into the **primary** session's embedded Claude — the user presses Enter. We never inject into a busy
  session and never call `claude -p`/the SDK.
- **The relevance scan is bounded and honest.** It walks the root + ≤2 levels (skipping
  `node_modules`/`bin`/`obj`/… ), short-circuits on the first manifest, and is computed once per
  workspace open. Its wall-clock timeout **fails open** (shows the dismissible card) rather than silently
  concluding "no manifest" — a deliberate, scoped exception to the no-safety-net-timeout rule.

Full design — model, triggers, the worktree-setup flow, and rationale — is in
[../specs/suggestions.md](../specs/suggestions.md).
