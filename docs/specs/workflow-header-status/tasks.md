# Workflow run in the agent pane header: tasks

Built in this order. Each group builds and tests on its own. Requirements: [requirements.md](requirements.md).

## Group 1: Run line in the native header

Satisfies R1, R3, R4, R5, R9, R10.

- [x] Add `chipOf` and `sessionRun` to `session-store.ts`, and use `chipOf` in `watchedUnseen`
- [x] Add `runDotColor` and `runTooltip` to `session-summary.ts`
- [x] Unit test both, plus `sessionRun`
- [x] Create `WorkflowRunStatus.tsx`
- [x] Render it in `AgentPane.tsx` before the shortcut `<Show>`
- [x] Add the base `.pane-run` styles to `layout.css`
- [ ] Extend `workflow-status-line.spec.ts`: text, dot colors, tooltip, run end, second session, reload, no stage count
- [ ] Record the "detail on demand" outcome in `docs/specs/workflows.md`

**Video:** run a workflow in a native pane. The run line appears, the dot changes color as the run moves from working to
waiting on the user's answer, the tooltip shows on hover, switching session shows the right run, and the line disappears
when the run ends.

## Group 2: Header layout

Satisfies R6, R7, R8.

- [ ] Add the truncation and badge rules to `layout.css`, and stop `.pane-label` shrinking
- [ ] Add `.app.compact .pane-run` to `mobile.css`
- [ ] E2e: badge order with and without the badge
- [ ] E2e: narrow pane truncates the workflow first, then the stage
- [ ] E2e: `mobile.spec.ts` hidden assertion
- [ ] Check truncation on WebKit

**Video:** drag the pane narrower with a long workflow and stage name, showing the workflow shorten while the stage stays
whole, the badge staying at the far right, and the run line gone in the mobile layout.

## Group 3: Terminal header

Satisfies R2.

- [ ] Render `WorkflowRunStatus` in the `App.tsx` terminal header with `selectedSession()`
- [ ] E2e: terminal journey driven by a fake-claude `mcp` step
- [ ] E2e: switching between terminal sessions updates the header

**Video:** run a workflow in a terminal Claude session. The "Claude Code" header shows the run line, and switching
sessions changes or clears it.
