import { createSignal } from "solid-js";
import { keyHint } from "../commands/key-hint";
import { onCommandsChanged } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { PullRequest } from "./pull-request-store";

const [commandsVersion, setCommandsVersion] = createSignal(0);
onCommandsChanged(() => setCommandsVersion((version) => version + 1));

/** Tooltip for a status-bar control that reviews the branch in Weavie — its PR when it has one. */
export function reviewBranchTitle(pr: PullRequest | null, branch: string): string {
  commandsVersion();
  const review = keyHint(CommandIds.diffAgainstBranchBase);
  if (pr === null) return `Review branch ${branch} in Weavie${review}`;
  const state = pr.state === "open" ? "" : ` ${pr.state}`;
  return `Review${state} PR #${pr.number} in Weavie${review} — open it on GitHub${keyHint(CommandIds.openCurrentPr)}`;
}
