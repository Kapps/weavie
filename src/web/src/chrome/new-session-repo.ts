/** The list entry that asks for a typed path. */
export const OTHER_REPO = "\0other";

/**
 * The list entry the composer both shows and sends: the first candidate the list still offers, else its first
 * entry, else "Other…". A repo that left the list is never named by a list that no longer shows it.
 */
export const chosenRepo = (
  candidates: readonly (string | null | undefined)[],
  repos: readonly string[],
): string =>
  candidates.find(
    (candidate): candidate is string =>
      candidate === OTHER_REPO || (candidate != null && repos.includes(candidate)),
  ) ??
  repos[0] ??
  OTHER_REPO;

/** The repo to ask the host for; a blank path is null, which names none. */
export const sentRepo = (text: string): string | null => text.trim() || null;
