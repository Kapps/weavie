import { createMemo } from "solid-js";
import { selectedSession } from "../bridge";
import type { FileIndex } from "../files/file-index";
import { selectedFileIndex } from "../files/session-file-index";
import { repoStateOf } from "../repo-state";

/** The listed files a repo's checkout-relative history names, in history order. */
export function projectRecentFiles(relativeHistory: readonly string[], index: FileIndex): string[] {
  const used = new Set<string>();
  const projected: string[] = [];
  for (const relative of relativeHistory) {
    const match = (index.find(relative) ?? index.findLoose(relative.replace(/[\\/]+$/, "")))?.abs;
    if (match !== undefined && !used.has(match)) {
      projected.push(match);
      used.add(match);
    }
  }
  return projected;
}

export interface RecentFilesState {
  files: readonly string[];
  loading: boolean;
  hasHistory: boolean;
}

/**
 * Selected-checkout paths in its repo's frecency order, intersected with its exact file index. The history is
 * checkout-relative, so recency follows a file across the repo's sessions.
 */
export const recentFilesState = createMemo<RecentFilesState>(() => {
  const session = selectedSession();
  if (session === null) {
    return { files: [], loading: false, hasHistory: false };
  }
  const history = repoStateOf(session).recentFiles;
  const index = selectedFileIndex();
  if (index.root === null || index.pending) {
    return { files: [], loading: true, hasHistory: history.length > 0 };
  }
  index.version;
  return {
    files: projectRecentFiles(history, index),
    loading: false,
    hasHistory: history.length > 0,
  };
});

export const recentFiles = createMemo<readonly string[]>(() => recentFilesState().files);
