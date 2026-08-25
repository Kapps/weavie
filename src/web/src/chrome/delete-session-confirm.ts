// How dirty the session's worktree is (host git status), driving the confirm friction: clean = one click,
// untracked = two-step confirm, modified = checkbox acknowledgement.
export type DeleteSessionState = "clean" | "untracked" | "modified";

/**
 * Whether the confirm is gated on the acknowledgement checkbox. Uncommitted changes and a checkout with no
 * branch both end in work that cannot be recovered, so they carry the same friction: without this a clean
 * branchless checkout is one Enter away from losing every commit made in it, while merely untracked files
 * ask twice.
 */
export function needsAcknowledgement(state: DeleteSessionState, branchless: boolean): boolean {
  return state === "modified" || branchless;
}
