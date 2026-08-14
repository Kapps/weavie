import { For, type JSX, Match, Show, Switch } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentMarkdown } from "./AgentMarkdown";
import { ApprovalActions, AuthenticationActions, InputRequestActions } from "./AgentPaneActions";
import { EditLocationActions, PlanActions } from "./AgentPaneEditActions";
import { AgentLinkedText } from "./AgentPaneLinks";
import type { AgentActivityStep, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import type { AgentSectionLabel } from "./pane-store";

export function TranscriptEntry(props: {
  detailsExpanded: boolean;
  entry: AgentTranscriptEntry;
  keyboardApprovalId: string | null;
  keyboardInputId: string | null;
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
        <AgentMedia message={props.entry.actionMessage} session={props.session} />
        <AgentRichContent message={props.entry.actionMessage} session={props.session} />
        <Show when={props.entry.detailCount > 0}>
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
          keyboardInputId={props.keyboardInputId}
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
  keyboardInputId: string | null;
  session: ClientSession;
}): JSX.Element {
  return (
    <Show when={props.entry.actionMessage}>
      {(message) => (
        <Switch>
          <Match when={message().type === "approval-requested" && props.entry.status === "pending"}>
            <ApprovalActions
              session={props.session}
              message={message()}
              answersToKeys={
                props.keyboardApprovalId !== null && message().itemId === props.keyboardApprovalId
              }
            />
          </Match>
          <Match
            when={message().type === "authentication-requested" && props.entry.status === "pending"}
          >
            <AuthenticationActions session={props.session} message={message()} />
          </Match>
          <Match when={message().type === "input-requested" && props.entry.status === "pending"}>
            <InputRequestActions
              session={props.session}
              message={message()}
              answersToKeys={
                props.keyboardInputId !== null && message().itemId === props.keyboardInputId
              }
            />
          </Match>
          <Match when={message().type === "edit-location"}>
            <EditLocationActions session={props.session} message={message()} />
          </Match>
          <Match
            when={(message().locations?.length ?? 0) > 0 || (message().diffs?.length ?? 0) > 0}
          >
            <EditLocationActions session={props.session} message={message()} />
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
        {activityDetailsSummary(props.entry, props.entry.detailCount)}
      </summary>
      <Show when={props.expanded}>
        <div class="agent-activity-list">
          <For each={props.steps}>
            {(step) => (
              <div class={`agent-activity-step agent-step-${step.tone}`}>
                <span class="agent-step-status">{step.status ?? "done"}</span>
                <span class="agent-step-label">{step.label}</span>
                <Show
                  when={
                    step.actionMessage?.type === "edit-location" ||
                    (step.actionMessage?.locations?.length ?? 0) > 0 ||
                    (step.actionMessage?.diffs?.length ?? 0) > 0
                  }
                >
                  <span class="agent-step-actions">
                    <Show when={step.actionMessage}>
                      {(message) => (
                        <EditLocationActions session={props.session} message={message()} />
                      )}
                    </Show>
                  </span>
                </Show>
                <Show when={step.detailText !== null}>
                  <pre>
                    <AgentLinkedText session={props.session} text={step.detailText ?? ""} />
                  </pre>
                </Show>
                <AgentRichContent message={step.actionMessage ?? null} session={props.session} />
              </div>
            )}
          </For>
        </div>
      </Show>
    </details>
  );
}

function AgentRichContent(props: {
  message: import("../bridge").AgentPaneUpdate | null;
  session: ClientSession;
}): JSX.Element {
  return (
    <For each={props.message?.content ?? []}>
      {(content) => {
        const source =
          content.mediaData !== null && content.mediaData !== undefined
            ? `data:${content.mediaType ?? "application/octet-stream"};base64,${content.mediaData}`
            : null;
        return (
          <div class="agent-entry-rich-content">
            <Show when={content.text !== null && content.text !== undefined}>
              <pre class="agent-entry-text">
                <AgentLinkedText session={props.session} text={content.text ?? ""} />
              </pre>
            </Show>
            <Show when={source !== null && content.mediaType?.startsWith("image/")}>
              <img
                class="agent-entry-media"
                src={source ?? ""}
                alt={content.name ?? "Agent tool output"}
              />
            </Show>
            <Show when={source !== null && !content.mediaType?.startsWith("image/")}>
              <a
                class="agent-entry-media"
                href={source ?? ""}
                download={content.name ?? "agent-tool-output"}
              >
                Download {content.name ?? "agent tool output"}
              </a>
            </Show>
            <Show when={content.resourceUri}>
              {(uri) => (
                <pre class="agent-entry-resource">
                  <AgentLinkedText
                    session={props.session}
                    text={
                      content.name === undefined || content.name === null
                        ? uri()
                        : `${content.name} (${uri()})`
                    }
                  />
                </pre>
              )}
            </Show>
          </div>
        );
      }}
    </For>
  );
}

function AgentMedia(props: {
  message: import("../bridge").AgentPaneUpdate | null;
  session: ClientSession;
}): JSX.Element {
  const source = (): string | null => {
    const message = props.message;
    return message?.mediaData !== null && message?.mediaData !== undefined && message.mediaType
      ? `data:${message.mediaType};base64,${message.mediaData}`
      : null;
  };
  return (
    <>
      <Show when={source() !== null && props.message?.mediaType?.startsWith("image/")}>
        <img class="agent-entry-media" src={source() ?? ""} alt="Agent-provided content" />
      </Show>
      <Show when={source() !== null && props.message?.mediaType?.startsWith("audio/")}>
        <a class="agent-entry-media" href={source() ?? ""} download="agent-provided-audio">
          Download agent-provided audio
        </a>
      </Show>
      <Show when={props.message?.resourceUri}>
        {(uri) => (
          <pre class="agent-entry-resource">
            {props.message?.type === "input-requested" && props.message.itemType === "url" ? (
              uri()
            ) : (
              <AgentLinkedText session={props.session} text={uri()} />
            )}
          </pre>
        )}
      </Show>
    </>
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
