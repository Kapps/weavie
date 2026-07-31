import { createMemo, createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";

/** The selected session's git branch + dirty flag, rendered by the terminal-column footer. */
export interface GitStatus {
  /** The checked-out branch, or null when the workspace isn't a git repo / HEAD is detached. */
  branch: string | null;
  /** Whether the worktree has uncommitted changes. */
  dirty: boolean;
}

const [statuses, setStatuses] = createSignal(new Map<ClientSession, GitStatus>());

export const gitStatus = createMemo(() => {
  const session = selectedSession();
  return session === null ? null : (statuses().get(session) ?? null);
});

registerSessionFeature((session) => {
  const off = session.feature("git").on<GitStatus>("status", (status) => {
    setStatuses((previous) => new Map(previous).set(session, status));
  });
  return () => {
    off();
    setStatuses((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});
