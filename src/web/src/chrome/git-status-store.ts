import { createSignal } from "solid-js";
import { onHostMessage } from "../bridge";

/** The active session's git branch + dirty flag, rendered by the terminal-column footer. */
export interface GitStatus {
  /** The checked-out branch, or null when the workspace isn't a git repo / HEAD is detached. */
  branch: string | null;
  /** Whether the worktree has uncommitted changes. */
  dirty: boolean;
}

// Top-level module signal so it survives HMR. Fed by the host's active-backend-gated `git-status` push (a
// background backend's traffic never reaches onHostMessage), so the footer always reflects the visible session.
const [status, setStatus] = createSignal<GitStatus | null>(null);

/** The active session's git status (reactive), or null before the first push. */
export const gitStatus = status;

onHostMessage((message) => {
  if (message.type === "git-status") {
    setStatus({ branch: message.branch, dirty: message.dirty });
  }
});
