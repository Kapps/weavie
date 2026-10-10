import { For, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { ModalShell } from "../chrome/ModalShell";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { TranscriptEntry } from "./AgentTranscriptEntry";

/** A subagent's whole read-only transcript. Esc closes it. */
export function AgentSubagentReader(props: {
  entry: AgentTranscriptEntry;
  expandedDetails: ReadonlySet<string>;
  keyboardRequestKey: string | null;
  onClose: () => void;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  session: ClientSession;
}): JSX.Element {
  const titleId = `subagent-reader-${props.entry.conversationId}`;
  return (
    <ModalShell
      class="agent-subagent-reader"
      labelledBy={titleId}
      onDismiss={props.onClose}
      onKeyDown={(event) => {
        if (event.key !== "Escape") return;
        event.preventDefault();
        event.stopPropagation();
        props.onClose();
      }}
    >
      <div class="confirm-title" id={titleId}>
        {props.entry.subagent!.name}
      </div>
      <Show when={props.entry.subagent!.task}>
        {(task) => <div class="agent-nested-task">{task()}</div>}
      </Show>
      <div class="agent-subagent-reader-body">
        <For each={props.entry.asideEntries ?? []}>
          {(entry) => (
            <TranscriptEntry
              expandedDetails={props.expandedDetails}
              entry={entry}
              keyboardRequestKey={props.keyboardRequestKey}
              latestPromptTurn={null}
              onDetailsToggle={props.onDetailsToggle}
              sectionLabel={null}
              session={props.session}
            />
          )}
        </For>
      </div>
      <div class="session-prompt-actions">
        <button type="button" class="session-prompt-btn" onClick={() => props.onClose()}>
          Close
        </button>
      </div>
    </ModalShell>
  );
}
