import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { sessionRun } from "../chrome/session-store";
import { runDotColor, runTooltip } from "../chrome/session-summary";

/** A pane header's line for the live workflow run `session` owns; nothing when it has none. */
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
