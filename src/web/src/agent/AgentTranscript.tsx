import { createSignal, For, type JSX, Show } from "solid-js";
import { AgentMarkdown } from "./AgentMarkdown";
import { ApprovalActions, EditLocationActions, InputRequestActions } from "./AgentPaneActions";
import { AgentLinkedText } from "./AgentPaneLinks";
import type { AgentActivityStep, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export function AgentTranscript(props: {
  entries: AgentTranscriptEntry[];
  slot: string | null;
}): JSX.Element {
  // Entries are rebuilt as updates arrive. Keep disclosure state outside the native <details> node
  // so replacing an entry does not close something the user is inspecting.
  const [expandedDetails, setExpandedDetails] = createSignal<ReadonlySet<string>>(new Set());

  return (
    <Show
      when={props.entries.length > 0}
      fallback={
        <div class="agent-empty">Codex is idle. Write a prompt to plan, change, or review.</div>
      }
    >
      <For each={props.entries}>
        {(entry, index) => (
          <TranscriptEntry
            detailsExpanded={expandedDetails().has(detailsKey(props.slot, entry.id))}
            entry={entry}
            onDetailsToggle={(open) => {
              const key = detailsKey(props.slot, entry.id);
              setExpandedDetails((current) => {
                if (current.has(key) === open) {
                  return current;
                }
                const next = new Set(current);
                if (open) {
                  next.add(key);
                } else {
                  next.delete(key);
                }
                return next;
              });
            }}
            result={isResultEntry(props.entries, index())}
            slot={props.slot}
          />
        )}
      </For>
    </Show>
  );
}

function TranscriptEntry(props: {
  detailsExpanded: boolean;
  entry: AgentTranscriptEntry;
  onDetailsToggle: (open: boolean) => void;
  result: boolean;
  slot: string | null;
}): JSX.Element {
  return (
    <article
      class={`agent-entry agent-entry-${props.entry.kind} agent-tone-${props.entry.tone}`}
      classList={{
        "agent-entry-edit": props.entry.actionMessage?.type === "edit-location",
        "agent-entry-result": props.result,
      }}
    >
      <Show when={props.result || showEntryHeader(props.entry)}>
        <div class="agent-entry-head" title={entryTitle(props.entry)}>
          <span class="agent-entry-label">{props.result ? "Result" : entryLabel(props.entry)}</span>
          <Show when={props.entry.status !== null}>
            <small class="agent-entry-status">{props.entry.status}</small>
          </Show>
        </div>
      </Show>
      <div class="agent-entry-main">
        <Show when={props.entry.summary !== null}>
          <div class="agent-entry-summary">
            <AgentLinkedText text={props.entry.summary ?? ""} />
          </div>
        </Show>
        <Show when={props.entry.text !== null}>
          <Show
            when={props.entry.kind === "message" && props.entry.tone === "assistant"}
            fallback={
              <pre class="agent-entry-text">
                <AgentLinkedText text={props.entry.text ?? ""} />
              </pre>
            }
          >
            <AgentMarkdown content={props.entry.text ?? ""} />
          </Show>
        </Show>
        <Show when={props.entry.details.length > 0}>
          <ActivityDetails
            entry={props.entry}
            expanded={props.detailsExpanded}
            onToggle={props.onDetailsToggle}
            steps={props.entry.details}
          />
        </Show>
        <EntryActions entry={props.entry} slot={props.slot} />
      </div>
    </article>
  );
}

function isResultEntry(entries: AgentTranscriptEntry[], index: number): boolean {
  const entry = entries[index];
  if (entry === undefined || entry.kind !== "message" || entry.tone !== "assistant") {
    return false;
  }
  for (let next = index + 1; next < entries.length; next += 1) {
    const candidate = entries[next];
    if (candidate?.kind === "message") {
      return candidate.tone === "user";
    }
  }
  return true;
}

function showEntryHeader(entry: AgentTranscriptEntry): boolean {
  return entry.kind !== "message" || entry.tone !== "assistant";
}

function EntryActions(props: { entry: AgentTranscriptEntry; slot: string | null }): JSX.Element {
  const message = props.entry.actionMessage;
  if (message === null) {
    return null;
  }
  if (message.type === "approval-requested" && props.entry.status === "pending") {
    return <ApprovalActions slot={props.slot} requestId={message.itemId} />;
  }
  if (message.type === "input-requested" && props.entry.status === "pending") {
    return <InputRequestActions slot={props.slot} message={message} />;
  }
  return message.type === "edit-location" ? <EditLocationActions target={message.text} /> : null;
}

function ActivityDetails(props: {
  entry: AgentTranscriptEntry;
  expanded: boolean;
  onToggle: (open: boolean) => void;
  steps: AgentActivityStep[];
}): JSX.Element {
  return (
    <details class="agent-activity-details" open={props.expanded}>
      {/* biome-ignore lint/a11y/noStaticElementInteractions: summary is the native details control. */}
      <summary
        onClick={(event) => {
          // Record intent synchronously. Native toggle events are queued and can otherwise arrive from a
          // transcript node that a later update has already replaced, restoring stale closed state.
          event.preventDefault();
          props.onToggle(!props.expanded);
        }}
      >
        {activityDetailsSummary(props.entry, props.steps.length)}
      </summary>
      <div class="agent-activity-list">
        <For each={props.steps}>
          {(step) => (
            <div class={`agent-activity-step agent-step-${step.tone}`}>
              <span class="agent-step-status">{step.status ?? "done"}</span>
              <span class="agent-step-label">{step.label}</span>
              <Show when={step.actionMessage?.type === "edit-location"}>
                <span class="agent-step-actions">
                  <EditLocationActions target={step.actionMessage?.text} />
                </span>
              </Show>
              <Show when={step.detailText !== null}>
                <pre>
                  <AgentLinkedText text={step.detailText ?? ""} />
                </pre>
              </Show>
            </div>
          )}
        </For>
      </div>
    </details>
  );
}

function detailsKey(slot: string | null, entryId: string): string {
  return `${slot ?? "detached"}:${entryId}`;
}

function activityDetailsSummary(entry: AgentTranscriptEntry, count: number): string {
  if (entry.label === "Earlier updates") {
    return `show ${count} earlier update${count === 1 ? "" : "s"}`;
  }
  if (entry.label === "Edits") {
    return `show ${count} edit${count === 1 ? "" : "s"}`;
  }
  return count === 1 ? "history" : `history ${count}`;
}

function entryLabel(entry: AgentTranscriptEntry): string {
  if (entry.kind === "message" && entry.tone === "user") {
    // A steer must say so — the user needs to see their message joined the running turn, not queued.
    return entry.label === "Steer" ? "Steer" : "Prompt";
  }
  switch (entry.label) {
    case "Interrupted":
      return "Interrupted";
    case "Permission":
      return "Permission";
    case "Warning":
      return "Warning";
    case "Working":
      return "Working";
    default:
      return entry.label;
  }
}

function entryTitle(entry: AgentTranscriptEntry): string {
  return entry.status === null ? entry.label : `${entry.label} ${entry.status}`;
}
