import { createResource, For, type JSX, Match, Show, Switch } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { Disclosure } from "./AgentDisclosure";
import { EditLocationActions } from "./AgentPaneEditActions";
import { deferredToolOutput } from "./AgentPaneHistoryAccumulator";
import { AgentLinkedText } from "./AgentPaneLinks";
import { normalizeText } from "./AgentPaneMessageFormat";
import type { AgentActivityStep, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentToolOutput } from "./AgentToolOutput";
import { loadToolOutput } from "./pane-store";

export function ActivityDetails(props: {
  entry: AgentTranscriptEntry;
  expanded: boolean;
  onToggle: (open: boolean) => void;
  session: ClientSession;
  steps: AgentActivityStep[];
}): JSX.Element {
  return (
    <Disclosure
      class="agent-activity-details"
      label={activityDetailsSummary(props.entry, props.entry.detailCount)}
      open={props.expanded}
      onToggle={props.onToggle}
    >
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
    </Disclosure>
  );
}

function ActivityStepOutput(props: {
  session: ClientSession;
  step: AgentActivityStep;
}): JSX.Element {
  const deferred = () => deferredToolOutput(props.step.actionMessage);
  const [loaded] = createResource(deferred, (message) => loadToolOutput(props.session, message));
  return (
    <Switch
      fallback={
        <StepOutput
          message={props.step.actionMessage ?? null}
          session={props.session}
          text={props.step.detailText}
        />
      }
    >
      <Match when={loaded.error !== undefined}>
        <span class="agent-entry-status" role="alert">
          Couldn't load this tool output (hide and show it to retry):{" "}
          {loaded.error instanceof Error ? loaded.error.message : String(loaded.error)}
        </span>
      </Match>
      <Match when={deferred() !== null && loaded.state !== "ready"}>
        <span class="agent-entry-status" role="status">
          Loading tool output…
        </span>
      </Match>
      <Match when={loaded.state === "ready" && loaded()}>
        {(message) => (
          <StepOutput
            message={message()}
            session={props.session}
            text={normalizeText(message().text)}
          />
        )}
      </Match>
    </Switch>
  );
}

function StepOutput(props: {
  message: AgentPaneUpdate | null;
  session: ClientSession;
  text: string | null;
}): JSX.Element {
  return (
    <>
      <Show when={props.text !== null}>
        <pre>
          <AgentLinkedText session={props.session} text={props.text ?? ""} />
        </pre>
      </Show>
      <AgentRichContent message={props.message} session={props.session} />
    </>
  );
}

function hasOutput(step: AgentActivityStep): boolean {
  return (
    step.detailText !== null ||
    (step.actionMessage?.content?.length ?? 0) > 0 ||
    deferredToolOutput(step.actionMessage) !== null
  );
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
