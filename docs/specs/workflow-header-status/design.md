# Workflow run in the agent pane header: detailed design

Requirements: [requirements.md](requirements.md). Approach: [overview.md](overview.md). Web-only.

## Interfaces

`src/web/src/chrome/session-store.ts`

```ts
/** The live workflow run `session` owns, or null when it has none. */
export function sessionRun(session: ClientSession | null): SessionRun | null;
```

It reads the session's chip, so it is reactive. A private `chipOf(session)` holds the owner lookup and also replaces the
same lookup in the `watchedUnseen` memo.

`src/web/src/chrome/session-summary.ts`

```ts
/** The CSS color of a run's header dot. */
export function runDotColor(state: SessionRunState): string;

/** A run's header tooltip: workflow, stage and state, then its reason on a second line when it has one. */
export function runTooltip(run: SessionRun): string;
```

`runDotColor` reads a `Record<SessionRunState, string>`, so a new state fails to compile until it has a color.
`runTooltip` reuses the private `RUN_PHRASE`.

`src/web/src/agent/WorkflowRunStatus.tsx`

```tsx
export function WorkflowRunStatus(props: { session: ClientSession | null }): JSX.Element {
  return (
    <Show when={sessionRun(props.session)}>
      {(run) => (
        <span class="pane-run" title={runTooltip(run())}>
          <span class="session-status" style={{ "--status-color": runDotColor(run().state) }} />
          <span class="pane-run-text">
            <span class="pane-run-workflow">{run().workflow}</span>
            <span class="pane-run-stage">{` · ${run().stage}`}</span>
          </span>
        </span>
      )}
    </Show>
  );
}
```

`AgentPane.tsx` passes `props.model.session` and `App.tsx` passes `selectedSession()`. Both render it directly before
their shortcut `<Show>`, because the CSS sibling rule depends on that order. In `AgentPane` the compact-only children
come first, which is harmless since the run line is hidden in compact.

## Data model

None added. `SessionRun` and `RailSession.run` already carry `workflow`, `stage`, `state` and `reason`, and the
host already publishes them on the `status` feed.

## Styles

`src/web/src/layout/layout.css`

```css
.pane-label { flex: none; white-space: nowrap; }
.pane-run { display: flex; align-items: center; gap: 6px; margin-left: auto;
  overflow: hidden; color: var(--dim); white-space: pre; }
.pane-run + .pane-shortcut { flex: none; margin-left: 0; }
.pane-run-text { display: flex; overflow: hidden; }
.pane-run-workflow, .pane-run-stage { overflow: hidden; text-overflow: ellipsis; }
.pane-run-stage { flex: none; max-width: 100%; }
```

- The separator is inside the stage span, so the stage's width cap is exact and the separator survives truncation.
- The stage takes `flex: none` rather than a shrink ratio, because a ratio still takes a fraction of a pixel from the
  stage and triggers its ellipsis early.
- Two auto margins would float the run mid-header, so the badge's is zeroed beside a run.
- Font and weight are inherited. Weight 600 and the accent color apply only to `.pane-label`.
- `.pane-label` is shared with the other panes. It gains only `flex: none; white-space: nowrap`.

`src/web/src/mobile/mobile.css` adds `.app.compact .pane-run` to the `display: none` list.

## Error handling

There are no new failure paths. No run, or a saved run that couldn't be loaded, renders nothing, and the session list
stays the place that reports an unreadable run. `patchOwned` drops a run message that arrives before its session's chip.
The design adds no retry or fallback for this; the reload test proves whether it can happen, and a failure there is a
defect to fix at the source.

## Testing strategy

Unit (Vitest):
- `session-summary.test.ts`: all seven state colors, and the tooltip with and without a reason.
- `session-store.test.ts`: `sessionRun` returns the owner's run, and null for another session and for null.

End to end, `headless` only since this is frontend-only. Scope locators to the surface, because the hidden terminal
header stays mounted.
- `workflow-status-line.spec.ts`, extended: header text, dot color through the states, tooltip, removal at run end, a
  second session with no run, reload, no stage count.
- Narrow pane: the workflow overflows and the stage doesn't, then the stage truncates when it alone can't fit.
- Badge order, with and without the badge.
- Terminal journey: a fake-claude `mcp` step calling `runCommand`, gated by `waitFile` until the workflow YAML exists.
  The ACP spec already drives `weavie.workflow.run` through `runCommand`; the terminal route is still unproven.
- `mobile.spec.ts`: the run line is hidden.

WebKit truncation is checked by `weavie-tester` in the real app. Web changes need `vite build` and `dotnet build`
before e2e, or the run serves a stale wwwroot. `src/web/e2e` isn't typechecked, so harness callers are checked by hand.
