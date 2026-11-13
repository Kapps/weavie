import { type JSX, Show, useContext } from "solid-js";
import { dashboardEntryId } from "./AgentPaneBlocks";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentRowContext } from "./agent-row-context";

/** The line a block added to a dashboard in a later turn leaves at that turn; it scrolls to the dashboard. */
export function AgentBlockAddedEntry(props: { entry: AgentTranscriptEntry }): JSX.Element {
  const row = useContext(AgentRowContext);
  return (
    <article class="agent-entry agent-entry-block-added agent-tone-assistant">
      {/* Outside the pane's transcript, such as in a BTW's tab, there is no row to scroll to. */}
      <Show when={row} fallback={<span class="agent-entry-summary">{props.entry.summary}</span>}>
        {(transcript) => (
          <button
            type="button"
            class="ui-row ui-row-content"
            title="Show Dashboard"
            onClick={() =>
              transcript().reveal(
                dashboardEntryId(props.entry.conversationId ?? null, props.entry.dashboardId!),
              )
            }
          >
            {props.entry.summary}
          </button>
        )}
      </Show>
    </article>
  );
}
