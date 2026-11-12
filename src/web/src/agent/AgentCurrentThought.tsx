import { createMemo, type JSX, Show } from "solid-js";
import { currentThoughtPreview } from "./AgentThoughtPreview";

/** A conversation's latest reasoning summary, independent of its message transcript. */
export function AgentCurrentThought(props: { text: string | null }): JSX.Element {
  const summary = createMemo(() => currentThoughtPreview(props.text ?? ""));
  return (
    <Show when={summary()}>
      <div class="agent-current-thought ui-row ui-row-compact" title={props.text ?? undefined}>
        <span class="agent-current-thought-text">{summary()}</span>
      </div>
    </Show>
  );
}
