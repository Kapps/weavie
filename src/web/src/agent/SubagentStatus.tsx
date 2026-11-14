import { type JSX, Show } from "solid-js";
import type { AgentSubagentInfo } from "./AgentPaneTranscriptTypes";
import type { AgentBackgroundItem } from "./agent-background-store";
import { backgroundStatus } from "./background-format";

/** A subagent's state and elapsed time, and the parent a nested spawn came from. */
export function SubagentStatus(props: { info: AgentSubagentInfo; now: number }): JSX.Element {
  return (
    <small class="agent-entry-status">
      {backgroundStatus(
        props.info.state,
        props.info.startedAtMs,
        props.info.completedAtMs,
        props.now,
        null,
      )}
      <Show when={props.info.via}>
        {(via) => <span class="agent-entry-via"> · via {via()}</span>}
      </Show>
    </small>
  );
}

/** A background task's state and elapsed time, followed by `usage` when given. */
export function TaskStatus(props: {
  item: AgentBackgroundItem;
  now: number;
  usage: AgentBackgroundItem["usage"];
}): JSX.Element {
  return (
    <small class="agent-entry-status">
      {backgroundStatus(
        props.item.state,
        props.item.startedAtMs,
        props.item.endedAtMs,
        props.now,
        props.usage,
      )}
    </small>
  );
}
