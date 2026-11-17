/** The repo the composer names until the user names another: the source session's, else the backend's first. */
export const defaultRepo = (sourceRepo: string | undefined, repos: readonly string[]): string =>
  sourceRepo ?? repos[0] ?? "";

/** The repo to ask the host for; a blank field is null, which leaves the choice to the host. */
export const sentRepo = (text: string): string | null => text.trim() || null;

/** The list entry that swaps the list for a typed path. */
export const OTHER_REPO = "\0other";
