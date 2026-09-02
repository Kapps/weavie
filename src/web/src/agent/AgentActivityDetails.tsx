import { For, type JSX, Show } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { EditLocationActions } from "./AgentPaneEditActions";
import { AgentLinkedText } from "./AgentPaneLinks";
import type { AgentActivityStep, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentToolOutput } from "./AgentToolOutput";

export function ActivityDetails(props: {
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
                <Show when={hasReviewTarget(step)}>
                  <span class="agent-step-actions">
                    <Show when={step.actionMessage}>
                      {(message) => (
                        <EditLocationActions session={props.session} message={message()} />
                      )}
                    </Show>
                  </span>
                </Show>
                <Show when={hasOutput(step)}>
                  <AgentToolOutput
                    renderOutput={() => <ActivityStepOutput session={props.session} step={step} />}
                  />
                </Show>
              </div>
            )}
          </For>
        </div>
      </Show>
    </details>
  );
}

function ActivityStepOutput(props: {
  session: ClientSession;
  step: AgentActivityStep;
}): JSX.Element {
  return (
    <>
      <Show when={props.step.detailText !== null}>
        <pre>
          <AgentLinkedText session={props.session} text={props.step.detailText ?? ""} />
        </pre>
      </Show>
      <AgentRichContent message={props.step.actionMessage ?? null} session={props.session} />
    </>
  );
}

function hasOutput(step: AgentActivityStep): boolean {
  return step.detailText !== null || (step.actionMessage?.content?.length ?? 0) > 0;
}

function hasReviewTarget(step: AgentActivityStep): boolean {
  return (
    step.actionMessage?.type === "edit-location" ||
    (step.actionMessage?.locations?.length ?? 0) > 0 ||
    (step.actionMessage?.diffs?.length ?? 0) > 0
  );
}

export function AgentRichContent(props: {
  message: AgentPaneUpdate | null;
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

function activityDetailsSummary(entry: AgentTranscriptEntry, count: number): string {
  if (entry.label === "Edits") {
    return `show ${count} edit${count === 1 ? "" : "s"}`;
  }
  return count === 1 ? "history" : `history ${count}`;
}
