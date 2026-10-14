import { createSignal } from "solid-js";
import { keyHint } from "../commands/key-hint";
import { onCommandsChanged } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { PullRequest } from "./pull-request-store";

const [commandsVersion, setCommandsVersion] = createSignal(0);
onCommandsChanged(() => setCommandsVersion((version) => version + 1));

function reviewHint(): string {
  commandsVersion();
  return keyHint(CommandIds.diffAgainstBranchBase);
}

/** Tooltip for a status-bar control that reviews the branch in Weavie. */
export function reviewBranchTitle(branch: string): string {
  return `Review branch ${branch} in Weavie${reviewHint()}`;
}

/** Tooltip for a status-bar control that reviews the branch's PR in Weavie, naming the GitHub shortcut. */
export function reviewPullRequestTitle(pr: PullRequest): string {
  const state = pr.state === "open" ? "" : ` ${pr.state}`;
  return `Review${state} PR #${pr.number} in Weavie${reviewHint()} — open it on GitHub${keyHint(CommandIds.openCurrentPr)}`;
}
