import { createEffect, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { gitStatus } from "../chrome/git-status-store";
import { type PullRequestStatus, pullRequestStatus } from "../chrome/pull-request-store";
import { setContext } from "../commands/context";
import { keyHint } from "../commands/key-hint";
import { onCommandsChanged, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { AgentControlPicker } from "./AgentControlPicker";
import { AgentUsageIndicator } from "./AgentUsageIndicator";
import { agentControlCommand } from "./agent-control-commands";
import {
  agentControlState,
  closeControlPicker,
  openControlAxis,
  openControlPicker,
} from "./agent-controls-store";
import { agentContextUsage } from "./agent-usage-store";

// The dim strip under the composer: provider-owned ACP controls, Git diff totals, and PR status.
export function AgentStatusLine(props: {
  compact: boolean;
  session: ClientSession | null;
}): JSX.Element {
  const state = (): ReturnType<typeof agentControlState> => agentControlState(props.session);
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
  const diffAdded = (): number | null => gitStatus()?.added ?? null;
  const diffRemoved = (): number | null => gitStatus()?.removed ?? null;
  const hasDiff = (): boolean => {
    const added = diffAdded();
    const removed = diffRemoved();
    return added !== null && removed !== null && (added > 0 || removed > 0);
  };
  const diffError = (): string | null => gitStatus()?.error ?? null;
  const diffTitle = (): string => {
    commandsVersion();
    return `Review diff against HEAD — ${diffAdded()} lines added, ${diffRemoved()} removed${keyHint(CommandIds.diffAgainstHead)}`;
  };
  const axisTitle = (axis: ReturnType<typeof state>["axes"][number]): string => {
    commandsVersion();
    const description = axis.description === null ? "" : ` — ${axis.description}`;
    const command = agentControlCommand(axis);
    const hint = command === null ? "" : keyHint(command);
    return `${axis.label}: ${axis.valueLabel}${hint}${description}`;
  };
  // Switching sessions abandons an open picker so it can't apply to the wrong session.
  createEffect(() => {
    props.session;
    closeControlPicker();
  });
  // Single owner of the composer's Enter/Escape gate: true whenever any control picker is open.
  createEffect(() => setContext("agentControlPickerOpen", openControlAxis() !== null));
  onCleanup(() => setContext("agentControlPickerOpen", false));

  return (
    <Show
      when={
        agentContextUsage(props.session) !== null ||
        state().axes.length > 0 ||
        hasDiff() ||
        diffError() !== null ||
        pullRequest() !== null ||
        prError() !== null
      }
    >
      <div class="agent-status-line" classList={{ "agent-status-line-compact": props.compact }}>
        <div class="agent-status-scroll">
          <AgentUsageIndicator session={props.session} />
          <Show when={hasDiff()}>
            <button
              type="button"
              class="agent-status-segment agent-status-diff"
              aria-label={diffTitle()}
              title={diffTitle()}
              onClick={() => void runCommandWithFeedback(CommandIds.diffAgainstHead)}
            >
              <span class="agent-status-diff-added">+{diffAdded()}</span>
              <span aria-hidden="true">/</span>
              <span class="agent-status-diff-removed">-{diffRemoved()}</span>
            </button>
          </Show>
          <Show when={diffError()}>
            {(error) => (
              <span
                class="agent-status-segment agent-status-unavailable"
                title={`Git could not calculate the diff against HEAD: ${error()}`}
              >
                Δ ?
              </span>
            )}
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
        <AgentControlPicker session={props.session} />
      </div>
    </Show>
  );
}
