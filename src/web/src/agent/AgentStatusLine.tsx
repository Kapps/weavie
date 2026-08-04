import { createEffect, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { gitStatus } from "../chrome/git-status-store";
import { type PullRequestStatus, pullRequestStatus } from "../chrome/pull-request-store";
import { setContext } from "../commands/context";
import { keyHint } from "../commands/key-hint";
import { onCommandsChanged, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { AgentControlPicker } from "./AgentControlPicker";
import { AgentModelPicker } from "./AgentModelPicker";
import {
  agentControlState,
  closeControlPicker,
  MODEL_AXIS,
  openControlAxis,
  openControlPicker,
} from "./agent-controls-store";

// The dim strip under the composer. First segment is the merged model → effort / Fast control (its picker is a
// cascading per-model submenu); the rest are provider-owned axes, the complete Review line counts, and PR status.
export function AgentStatusLine(props: {
  compact: boolean;
  reviewAdded: number;
  reviewFileCount: number;
  reviewRemoved: number;
  session: ClientSession | null;
}): JSX.Element {
  const state = (): ReturnType<typeof agentControlState> => agentControlState(props.session);
  const modelLabel = (): string => state().modelControl.valueLabel;
  const hasModel = (): boolean => state().modelControl.models.length > 0;
  const [commandsVersion, setCommandsVersion] = createSignal(0);
  onCleanup(onCommandsChanged(() => setCommandsVersion((version) => version + 1)));
  const prStatus = () => pullRequestStatus(props.session);
  const pullRequest = () => {
    const status = prStatus();
    return status !== null && status.branch === gitStatus()?.branch ? status.pullRequest : null;
  };
  const prError = (): string | null => {
    const status = prStatus();
    return status !== null && status.branch === gitStatus()?.branch ? status.error : null;
  };
  const pullRequestTitle = (pr: NonNullable<PullRequestStatus["pullRequest"]>): string => {
    commandsVersion();
    const state = pr.state === "open" ? "" : ` ${pr.state}`;
    const refreshError = prError() === null ? "" : ` — last refresh failed: ${prError()}`;
    return `Open${state} PR #${pr.number} in browser${keyHint(CommandIds.openCurrentPr)}${refreshError}`;
  };
  const pullRequestLabel = (pr: NonNullable<PullRequestStatus["pullRequest"]>): string =>
    pr.state === "open"
      ? `#${pr.number}`
      : `#${pr.number} · ${pr.state === "merged" ? "Merged" : "Closed"}`;
  const reviewTitle = (): string => {
    commandsVersion();
    const files = props.reviewFileCount === 1 ? "1 file" : `${props.reviewFileCount} files`;
    return `Open review — ${props.reviewAdded} lines added, ${props.reviewRemoved} removed across ${files}${keyHint(CommandIds.reviewOpen)}`;
  };
  const axisTitle = (axis: ReturnType<typeof state>["axes"][number]): string => {
    commandsVersion();
    return `${axis.label}: ${axis.valueLabel} — click to change${
      axis.commandId === null ? "" : keyHint(axis.commandId)
    }`;
  };
  // Switching sessions abandons an open picker so it can't apply to the wrong session.
  createEffect(() => {
    props.session;
    closeControlPicker();
  });
  // Single owner of the composer's Enter/Escape gate: true whenever any control picker (model or axis) is open.
  createEffect(() => setContext("agentControlPickerOpen", openControlAxis() !== null));
  onCleanup(() => setContext("agentControlPickerOpen", false));

  return (
    <Show
      when={
        hasModel() ||
        state().axes.length > 0 ||
        props.reviewAdded > 0 ||
        props.reviewRemoved > 0 ||
        pullRequest() !== null ||
        prError() !== null
      }
    >
      <div class="agent-status-line" classList={{ "agent-status-line-compact": props.compact }}>
        <div class="agent-status-scroll">
          <Show when={hasModel()}>
            <button
              type="button"
              class="agent-status-segment agent-status-model"
              title={`Model — ${modelLabel()} — click to change model, effort, or Fast Mode`}
              onClick={() => openControlPicker(MODEL_AXIS)}
            >
              <span class="agent-status-value">{modelLabel()}</span>
            </button>
          </Show>
          <For each={state().axes}>
            {(axis) => (
              <button
                type="button"
                class="agent-status-segment"
                title={axisTitle(axis)}
                onClick={() => openControlPicker(axis.id)}
              >
                <span class="agent-status-key">{axis.label}</span>
                <span class="agent-status-value">{axis.valueLabel}</span>
              </button>
            )}
          </For>
          <Show when={props.reviewAdded > 0 || props.reviewRemoved > 0}>
            <button
              type="button"
              class="agent-status-segment agent-status-review"
              aria-label={reviewTitle()}
              title={reviewTitle()}
              onClick={() => void runCommandWithFeedback(CommandIds.reviewOpen)}
            >
              <span class="agent-status-review-added">+{props.reviewAdded}</span>
              <span aria-hidden="true">/</span>
              <span class="agent-status-review-removed">-{props.reviewRemoved}</span>
            </button>
          </Show>
          <Show when={pullRequest()}>
            {(pr) => (
              <button
                type="button"
                class="agent-status-segment agent-status-pr"
                title={pullRequestTitle(pr())}
                onClick={() => void runCommandWithFeedback(CommandIds.openCurrentPr)}
              >
                {pullRequestLabel(pr())}
              </button>
            )}
          </Show>
          <Show when={pullRequest() === null ? prError() : null}>
            {(error) => (
              <span
                class="agent-status-segment agent-status-unavailable"
                title={`Pull request detection unavailable: ${error()}`}
              >
                PR ?
              </span>
            )}
          </Show>
        </div>
        <AgentModelPicker session={props.session} />
        <AgentControlPicker session={props.session} />
      </div>
    </Show>
  );
}
