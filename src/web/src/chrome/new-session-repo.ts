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

/** The list entry that swaps the list for a typed path. */
export const OTHER_REPO = "\0other";

/** Each repo as a list entry: named by its folder, or by its whole path when another repo shares that name. */
export function repoOptions(repos: readonly string[]): { repo: string; name: string }[] {
  const leaf = (repo: string): string => repo.split(/[\\/]/).filter(Boolean).at(-1) ?? repo;
  return repos.map((repo) => ({
    repo,
    name: repos.some((other) => other !== repo && leaf(other) === leaf(repo)) ? repo : leaf(repo),
  }));
}
