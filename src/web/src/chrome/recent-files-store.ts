import { createMemo } from "solid-js";
import { selectedSession } from "../bridge";
import { normalizePath, repoRelativePath } from "../editor/fs-path";
import { selectedFileIndex } from "../files/session-files";
import { repoStateOf } from "../repo-state";

export function projectRecentFiles(
  relativeHistory: readonly string[],
  root: string,
  indexedFiles: readonly string[],
): string[] {
  const exact = new Map<string, string>();
  const loose = new Map<string, string[]>();
  for (const path of indexedFiles) {
    const relative = repoRelativePath(root, path);
    exact.set(relative, path);
    const key = normalizePath(relative);
    const aliases = loose.get(key);
    if (aliases === undefined) {
      loose.set(key, [path]);
    } else {
      aliases.push(path);
    }
  }
  const used = new Set<string>();
  const projected: string[] = [];
  for (const relative of relativeHistory) {
    const exactMatch = exact.get(relative);
    const aliases = exactMatch === undefined ? loose.get(normalizePath(relative)) : undefined;
    const match = exactMatch ?? (aliases?.length === 1 ? aliases[0] : undefined);
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
  return {
    files: projectRecentFiles(history, index.root, index.files),
    loading: false,
    hasHistory: history.length > 0,
  };
});

export const recentFiles = createMemo<readonly string[]>(() => recentFilesState().files);
