import { createMemo, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { ActivityStepOutput } from "./AgentActivityDetails";
import { AgentPathLink } from "./AgentPaneLinks";
import { agentBackground, backgroundRunning } from "./agent-background-store";
import { backgroundStatus } from "./background-format";
import { liveNow } from "./live-clock";
import { agentPaneModel } from "./pane-store";

/** One background task as an editor tab: its state, timing, usage, activity, and result, following it live. */
export default function AgentTaskTab(props: {
  session: ClientSession;
  path: string;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  let host!: HTMLDivElement;
  const item = createMemo(() =>
    agentBackground(props.session).find((candidate) => candidate.id === props.path),
  );
  const step = createMemo(() => {
    const id = item()?.transcriptItemId;
    return id == null ? undefined : agentPaneModel(props.session)?.activityStep(id);
  });
  const now = liveNow(() => item() !== undefined && backgroundRunning(item()!));
  onMount(() => onCleanup(props.bind(host)));

  return (
    <div class="editor-agent-view agent-body" data-kind="editor" tabindex="0" ref={host}>
      <Show
        when={item()}
        fallback={
          <div class="editor-plan-notice">
            This task is no longer listed: finished work clears at the agent's next prompt.
          </div>
        }
      >
        {(task) => (
          <article
            class={`agent-entry agent-entry-task agent-tone-${task().state === "failed" ? "error" : "assistant"}`}
            data-agent-task={task().id}
            data-state={task().state}
          >
            <div class="agent-entry-head">
              <span class="agent-entry-label">Task · {task().type}</span>
              <Show when={backgroundRunning(task())}>
                <span class="agent-working-spinner" aria-hidden="true" />
              </Show>
              <small class="agent-entry-status">
                {backgroundStatus(
                  task().state,
                  task().startedAtMs,
                  task().endedAtMs,
                  now(),
                  task().usage,
                )}
              </small>
            </div>
            <div class="agent-entry-main">
              <div class="agent-entry-summary">{task().name}</div>
              <dl class="agent-task-facts">
                <dt>Started</dt>
                <dd>{new Date(task().startedAtMs).toLocaleString()}</dd>
                <Show when={task().detail}>
                  {(detail) => (
                    <>
                      <dt>Detail</dt>
                      <dd>{detail()}</dd>
                    </>
                  )}
                </Show>
                <Show when={task().lastActivity}>
                  {(activity) => (
                    <>
                      <dt>Last activity</dt>
                      <dd>{activity()}</dd>
                    </>
                  )}
                </Show>
                <Show when={task().summary}>
                  {(summary) => (
                    <>
                      <dt>{task().state === "failed" ? "Error" : "Result"}</dt>
                      <dd data-task-summary>{summary()}</dd>
                    </>
                  )}
                </Show>
                <Show when={task().outputPath}>
                  {(path) => (
                    <>
                      <dt>Output file</dt>
                      <dd>
                        <AgentPathLink session={props.session} path={path()} text={path()} />
                      </dd>
                    </>
                  )}
                </Show>
              </dl>
              <Show when={step()}>
                {(tool) => (
                  <section class="agent-task-tool" data-task-tool>
                    <div class="agent-entry-label">
                      Tool call · {tool().label} · {tool().status ?? "done"}
                    </div>
                    <ActivityStepOutput session={props.session} step={tool()} />
                  </section>
                )}
              </Show>
            </div>
          </article>
        )}
      </Show>
    </div>
  );
}
