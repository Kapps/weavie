import { ChevronDown } from "lucide-solid";
import { createEffect, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { ContextMenu, type ContextMenuState } from "../chrome/ContextMenu";
import { gitStatus } from "../chrome/git-status-store";
import { scrollWheelHorizontally } from "../chrome/horizontal-overflow";
import { currentPullRequestStatus, type PullRequest } from "../chrome/pull-request-store";
import { reviewPullRequestTitle } from "../chrome/review-branch-title";
import { setContext } from "../commands/context";
import { keyHint } from "../commands/key-hint";
import {
  captureCommandRunner,
  onCommandsChanged,
  runCommandWithFeedback,
} from "../commands/registry";
import { CommandIds } from "../commands/types";
import { prCommentsFor } from "../editor/pr-comments/pr-comments-store";
import { AgentControlPicker } from "./AgentControlPicker";
import { AgentUsageIndicator } from "./AgentUsageIndicator";
import { agentControlCommand } from "./agent-control-commands";
import {
  agentControlState,
  closeControlPicker,
  openControlAxis,
  toggleControlPicker,
} from "./agent-controls-store";
import { agentUsage } from "./agent-usage-store";

// The dim strip under the composer: provider-owned ACP controls, Git diff totals, and PR status.
export function AgentStatusLine(props: {
  compact: boolean;
  session: ClientSession | null;
}): JSX.Element {
  const state = (): ReturnType<typeof agentControlState> => agentControlState(props.session);
  const [commandsVersion, setCommandsVersion] = createSignal(0);
  onCleanup(onCommandsChanged(() => setCommandsVersion((version) => version + 1)));
  const prStatus = () => currentPullRequestStatus(props.session);
  const pullRequest = () => prStatus()?.pullRequest ?? null;
  const prError = (): string | null => prStatus()?.error ?? null;
  const reviewTitle = (pr: PullRequest): string => {
    const refreshError = prError() === null ? "" : ` — last refresh failed: ${prError()}`;
    const commentError = comments()?.error;
    const commentNote =
      commentError == null ? "" : ` — review comments failed to load: ${commentError}`;
    return `${reviewPullRequestTitle(pr)}${refreshError}${commentNote}`;
  };
  const comments = () => prCommentsFor(props.session);
  const pullRequestLabel = (pr: PullRequest): string => {
    const threads = comments()?.set?.threads.length ?? 0;
    const state = pr.state === "open" ? "" : ` · ${pr.state === "merged" ? "Merged" : "Closed"}`;
    return `#${pr.number}${state}${threads === 0 ? "" : ` · ${threads} ${threads === 1 ? "thread" : "threads"}`}`;
  };
  const diffAdded = (): number | null => gitStatus()?.added ?? null;
  const diffRemoved = (): number | null => gitStatus()?.removed ?? null;
  const hasDiff = (): boolean => {
    const added = diffAdded();
    const removed = diffRemoved();
    return added !== null && removed !== null && (added > 0 || removed > 0);
  };
  const diffError = (): string | null => gitStatus()?.error ?? null;
  const diffTitle = (): string =>
    `${diffAdded()} lines added, ${diffRemoved()} removed since HEAD — review a diff`;
  // Clicking the totals offers the session's diffs; each opens on the session overview.
  const [diffMenu, setDiffMenu] = createSignal<ContextMenuState | null>(null);
  const openDiffMenu = (anchor: HTMLElement): void => {
    const rect = anchor.getBoundingClientRect();
    setDiffMenu({
      x: rect.left,
      y: rect.top,
      header: "Review a diff",
      entries: [
        { commandId: CommandIds.reviewOverview, label: "Current review" },
        { commandId: CommandIds.diffAgainstHead, label: "Uncommitted" },
        { commandId: CommandIds.diffAgainstParent, label: "Last commit" },
        { commandId: CommandIds.diffAgainstBranchBase, label: "Branch" },
      ],
      runCommand: captureCommandRunner(),
    });
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
        agentUsage(props.session)?.contextWindow != null ||
        state().axes.length > 0 ||
        hasDiff() ||
        diffError() !== null ||
        pullRequest() !== null ||
        prError() !== null
      }
    >
      <div class="agent-status-line" classList={{ "agent-status-line-compact": props.compact }}>
        <div
          class="agent-status-scroll"
          onFocusIn={(event) => {
            if (event.target instanceof HTMLElement) {
              event.target.scrollIntoView({ block: "nearest", inline: "nearest" });
            }
          }}
          onWheel={scrollWheelHorizontally}
        >
          <AgentUsageIndicator session={props.session} />
          <Show when={hasDiff()}>
            <button
              type="button"
              class="agent-status-segment agent-status-diff"
              aria-label={diffTitle()}
              title={diffTitle()}
              aria-haspopup="menu"
              aria-expanded={diffMenu() !== null}
              onClick={(event) => openDiffMenu(event.currentTarget)}
            >
              <span class="agent-status-diff-added">+{diffAdded()}</span>
              <span aria-hidden="true">/</span>
              <span class="agent-status-diff-removed">-{diffRemoved()}</span>
              <ChevronDown size="1em" class="agent-status-diff-more" aria-hidden="true" />
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
                class="agent-status-segment agent-status-axis"
                title={axisTitle(axis)}
                aria-label={`${axis.label} ${axis.valueLabel}`}
                onClick={() => toggleControlPicker(axis.id)}
              >
                <span class="agent-status-value">{axis.valueLabel}</span>
              </button>
            )}
          </For>
          <Show when={pullRequest()}>
            {(pr) => (
              <button
                type="button"
                class="agent-status-segment agent-status-pr"
                classList={{ "agent-status-warn": comments()?.error != null }}
                title={reviewTitle(pr())}
                onClick={() => void runCommandWithFeedback(CommandIds.diffAgainstBranchBase)}
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
      <Show when={diffMenu()}>
        {(menu) => <ContextMenu menu={menu()} onClose={() => setDiffMenu(null)} />}
      </Show>
    </Show>
  );
}
