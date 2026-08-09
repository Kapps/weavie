import { createMemo } from "solid-js";
import { selectedSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

/** The selected session's Git branch, dirty flag, and complete working-tree totals against HEAD. */
export interface GitStatus {
  /** The checked-out branch, or null when the workspace isn't a git repo / HEAD is detached. */
  branch: string | null;
  /** Whether the worktree has uncommitted changes. */
  dirty: boolean;
  /** Lines added by tracked changes and untracked files against HEAD, or null when Git cannot diff HEAD. */
  added: number | null;
  /** Lines removed by tracked changes against HEAD, or null when Git cannot diff HEAD. */
  removed: number | null;
  /** Why Git could not calculate the HEAD diff, or null when the counts are authoritative. */
  error: string | null;
}

const statusFor = createSessionFeatureValue<GitStatus, GitStatus>(
  "git",
  "status",
  (status) => status,
);

export const gitStatus = createMemo(() => statusFor(selectedSession()));
