/** The distinct repo roots among a backend's sessions, in catalog order. */
export const backendRepos = (
  sessions: readonly { backendId: string; repo: string }[],
  backendId: string,
): string[] => [
  ...new Set(
    sessions.filter((session) => session.backendId === backendId).map((session) => session.repo),
  ),
];

/** The repo the composer names until the user names another: the source session's, else the backend's first. */
export const defaultRepo = (sourceRepo: string | undefined, repos: readonly string[]): string =>
  sourceRepo ?? repos[0] ?? "";

/** The repo to ask the host for; a blank field is null, which leaves the choice to the host. */
export const sentRepo = (text: string): string | null => text.trim() || null;
