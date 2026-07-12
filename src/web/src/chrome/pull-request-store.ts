import { createSignal } from "solid-js";
import { onSessionMessage } from "../bridge";

export interface PullRequestStatus {
  branch: string | null;
  pullRequest: { number: number; url: string } | null;
  error: string | null;
}

const [statuses, setStatuses] = createSignal<Record<string, PullRequestStatus>>({});
const key = (backendId: string, slot: string): string => `${backendId}\0${slot}`;

export function pullRequestStatus(
  backendId: string,
  slot: string | null,
): PullRequestStatus | null {
  return slot === null ? null : (statuses()[key(backendId, slot)] ?? null);
}

onSessionMessage((message, backendId) => {
  if (message.type === "pull-request-status") {
    setStatuses((previous) => ({
      ...previous,
      [key(backendId, message.slot)]: {
        branch: message.branch,
        pullRequest: message.pullRequest,
        error: message.error,
      },
    }));
  }
});
