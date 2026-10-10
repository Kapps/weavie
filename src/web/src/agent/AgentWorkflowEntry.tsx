import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyHint } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { agentBackground, backgroundRunning } from "./agent-background-store";
import { backgroundStatus } from "./background-format";
import { liveNow } from "./live-clock";

/** A background workflow, anchored where it started; its live state and Stop come from the background item. */
export function AgentWorkflowEntry(props: {
  entry: AgentTranscriptEntry;
  session: ClientSession;
}): JSX.Element {
  const itemId = () => props.entry.actionMessage?.itemId ?? null;
  const item = () => agentBackground(props.session).find((candidate) => candidate.id === itemId());
  const state = () => item()?.state ?? props.entry.status ?? "running";
  const now = liveNow(() => item() !== undefined && backgroundRunning(item()!));
  const status = () => {
    const live = item();
    const message = props.entry.actionMessage;
    return backgroundStatus(
      state(),
      live?.startedAtMs ?? message?.startedAtMs ?? null,
      live?.endedAtMs ?? message?.completedAtMs ?? null,
      now(),
      live?.usage ?? null,
    );
  };

  return (
    <article
      class={`agent-entry agent-entry-workflow agent-tone-${state() === "failed" ? "error" : "assistant"}`}
      data-agent-workflow={itemId() ?? undefined}
      data-state={state()}
    >
      <div class="agent-entry-head">
        <span class="agent-entry-label">Workflow</span>
        <Show when={state() === "running" || state() === "paused"}>
          <span class="agent-working-spinner" aria-hidden="true" />
        </Show>
        <small class="agent-entry-status">{status()}</small>
        <Show when={item()?.canStop}>
          <button
            type="button"
            class="agent-entry-rewind agent-entry-open"
            title={`Stop workflow${liveKeyHint(CommandIds.stopBackgroundTask)}`}
            onClick={() =>
              void runCommandWithFeedback(CommandIds.stopBackgroundTask, { id: itemId() })
            }
          >
            Stop
          </button>
        </Show>
      </div>
      <div class="agent-entry-main">
        <div class="agent-entry-summary">{props.entry.summary}</div>
        <Show when={item()?.lastActivity ?? props.entry.text}>
          {(detail) => <div class="agent-entry-text">{detail()}</div>}
        </Show>
      </div>
    </article>
  );
}
