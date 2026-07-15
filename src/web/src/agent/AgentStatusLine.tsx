import { createEffect, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { gitStatus } from "../chrome/git-status-store";
import { pullRequestStatus } from "../chrome/pull-request-store";
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
// cascading per-model submenu); the rest are provider-owned axes. Values are provider-neutral — the web
// never learns their provider-specific meaning.
export function AgentStatusLine(props: { backendId: string; slot: string | null }): JSX.Element {
  const state = (): ReturnType<typeof agentControlState> => agentControlState(props.slot);
  const modelLabel = (): string => state().modelControl.valueLabel;
  const hasModel = (): boolean => state().modelControl.models.length > 0;
  const [commandsVersion, setCommandsVersion] = createSignal(0);
  onCleanup(onCommandsChanged(() => setCommandsVersion((version) => version + 1)));
  const prStatus = () => pullRequestStatus(props.backendId, props.slot);
  const pullRequest = () => {
    const status = prStatus();
    return status !== null && status.branch === gitStatus()?.branch ? status.pullRequest : null;
  };
  const prError = (): string | null => {
    const status = prStatus();
    return status !== null && status.branch === gitStatus()?.branch ? status.error : null;
  };
  const pullRequestTitle = (number: number): string => {
    commandsVersion();
    return `Open PR #${number} in browser${keyHint(CommandIds.openCurrentPr)}`;
  };
  const axisTitle = (axis: ReturnType<typeof state>["axes"][number]): string => {
    commandsVersion();
    return `${axis.label}: ${axis.valueLabel} — click to change${
      axis.commandId === null ? "" : keyHint(axis.commandId)
    }`;
  };
  // Switching sessions abandons an open picker so it can't apply to the wrong session.
  createEffect(() => {
    props.slot;
    closeControlPicker();
  });
  // Single owner of the composer's Enter/Escape gate: true whenever any control picker (model or axis) is open.
  createEffect(() => setContext("agentControlPickerOpen", openControlAxis() !== null));
  onCleanup(() => setContext("agentControlPickerOpen", false));

  return (
    <Show
      when={hasModel() || state().axes.length > 0 || pullRequest() !== null || prError() !== null}
    >
      <div class="agent-status-line">
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
        <Show when={pullRequest()}>
          {(pr) => (
            <button
              type="button"
              class="agent-status-segment agent-status-pr"
              title={pullRequestTitle(pr().number)}
              onClick={() => void runCommandWithFeedback(CommandIds.openCurrentPr)}
            >
              #{pr().number}
            </button>
          )}
        </Show>
        <Show when={prError()}>
          {(error) => (
            <span
              class="agent-status-segment agent-status-unavailable"
              title={`Pull request detection unavailable: ${error()}`}
            >
              PR ?
            </span>
          )}
        </Show>
        <AgentModelPicker backendId={props.backendId} slot={props.slot} />
        <AgentControlPicker backendId={props.backendId} slot={props.slot} />
      </div>
    </Show>
  );
}
