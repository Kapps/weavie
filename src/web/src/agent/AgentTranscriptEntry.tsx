import { For, type JSX, Match, Show, Switch } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentMarkdown } from "./AgentMarkdown";
import {
  ApprovalActions,
  EditLocationActions,
  InputRequestActions,
  PlanActions,
} from "./AgentPaneActions";
import { AgentLinkedText } from "./AgentPaneLinks";
import type { AgentActivityStep, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import type { AgentSectionLabel } from "./pane-store";

export function TranscriptEntry(props: {
  detailsExpanded: boolean;
  entry: AgentTranscriptEntry;
  keyboardApprovalId: string | null;
  onDetailsToggle: (open: boolean) => void;
  sectionLabel: AgentSectionLabel | null;
  session: ClientSession;
}): JSX.Element {
  return (
    <article
      class={`agent-entry agent-entry-${props.entry.kind} agent-tone-${props.entry.tone}`}
      classList={{
        "agent-entry-edit": props.entry.actionMessage?.type === "edit-location",
        "agent-entry-result": props.sectionLabel !== null,
      }}
    >
      <Show when={props.sectionLabel !== null || showEntryHeader(props.entry)}>
        <div class="agent-entry-head" title={entryTitle(props.entry)}>
          <span class="agent-entry-label">
            {props.entry.kind === "plan" && props.sectionLabel !== null
              ? `${props.sectionLabel} · Plan`
              : (props.sectionLabel ?? entryLabel(props.entry))}
          </span>
          <Show when={props.entry.status !== null}>
            <small class="agent-entry-status">{props.entry.status}</small>
          </Show>
        </div>
      </Show>
      <div class="agent-entry-main">
        <Show when={props.entry.summary !== null}>
          <div class="agent-entry-summary">
            <AgentLinkedText session={props.session} text={props.entry.summary ?? ""} />
          </div>
        </Show>
        <Show when={props.entry.text !== null}>
          <Show
            when={props.entry.kind === "message" && props.entry.tone === "assistant"}
            fallback={
              <pre class="agent-entry-text">
                <AgentLinkedText session={props.session} text={props.entry.text ?? ""} />
              </pre>
            }
          >
            <AgentMarkdown
              cacheKey={props.entry}
              content={props.entry.text ?? ""}
              renderMermaid={!props.entry.streaming}
              session={props.session}
            />
          </Show>
        </Show>
        <Show when={props.entry.details.length > 0}>
          <ActivityDetails
            entry={props.entry}
            expanded={props.detailsExpanded}
            onToggle={props.onDetailsToggle}
            session={props.session}
            steps={props.entry.details}
          />
        </Show>
        <EntryActions
          entry={props.entry}
          keyboardApprovalId={props.keyboardApprovalId}
          session={props.session}
        />
      </div>
    </article>
  );
}

function showEntryHeader(entry: AgentTranscriptEntry): boolean {
  return entry.kind !== "message" || entry.tone !== "assistant";
}

function EntryActions(props: {
  entry: AgentTranscriptEntry;
  keyboardApprovalId: string | null;
  session: ClientSession;
}): JSX.Element {
  return (
    <Show when={props.entry.actionMessage}>
      {(message) => (
        <Switch>
          <Match when={message().type === "approval-requested" && props.entry.status === "pending"}>
            <ApprovalActions
              session={props.session}
              requestId={message().itemId}
              answersToKeys={
                props.keyboardApprovalId !== null && message().itemId === props.keyboardApprovalId
              }
            />
          </Match>
          <Match when={message().type === "input-requested" && props.entry.status === "pending"}>
            <InputRequestActions session={props.session} message={message()} />
          </Match>
          <Match when={message().type === "edit-location"}>
            <EditLocationActions session={props.session} target={message().text} />
          </Match>
          <Match when={message().type === "item-completed" && message().itemType === "plan"}>
            <PlanActions message={message()} session={props.session} />
          </Match>
        </Switch>
      )}
    </Show>
  );
}

function ActivityDetails(props: {
  entry: AgentTranscriptEntry;
  expanded: boolean;
  onToggle: (open: boolean) => void;
  session: ClientSession;
  steps: AgentActivityStep[];
}): JSX.Element {
  return (
    <details class="agent-activity-details" open={props.expanded}>
      {/* biome-ignore lint/a11y/noStaticElementInteractions: summary is the native details control. */}
      <summary
        onClick={(event) => {
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
                  <EditLocationActions session={props.session} target={step.actionMessage?.text} />
                </span>
              </Show>
              <Show when={step.detailText !== null}>
                <pre>
                  <AgentLinkedText session={props.session} text={step.detailText ?? ""} />
                </pre>
              </Show>
            </div>
          )}
        </For>
      </div>
    </details>
  );
}

function activityDetailsSummary(entry: AgentTranscriptEntry, count: number): string {
  if (entry.label === "Edits") {
    return `show ${count} edit${count === 1 ? "" : "s"}`;
  }
  return count === 1 ? "history" : `history ${count}`;
}

function entryLabel(entry: AgentTranscriptEntry): string {
  if (entry.kind === "message" && entry.tone === "user") {
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
