import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { sessionInfo } from "../chrome/session-store";
import { sessionTooltip, statusDot } from "../chrome/session-summary";

/** A pane header's line for the live workflow run `session` owns; nothing when it has none. */
export function WorkflowRunStatus(props: { session: ClientSession | null }): JSX.Element {
  return (
    <Show when={sessionInfo(props.session)}>
      {(session) => (
        <Show when={session().run}>
          {(run) => (
            <span class="pane-run" title={sessionTooltip(session())}>
              <span class={`session-status status-${statusDot(session())}`} />
              <span class="pane-run-text">
                <span class="pane-run-workflow">{run().workflow}</span>
                <span class="pane-run-stage">{` · ${run().stage}`}</span>
              </span>
            </span>
          )}
        </Show>
      )}
    </Show>
  );
}
