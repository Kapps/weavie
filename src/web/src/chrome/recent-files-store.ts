import { type Accessor, createMemo, createRoot } from "solid-js";
import { type ClientSession, registerSessionFeature } from "../bridge";
import type { FileIndex } from "../files/file-index";
import { fileIndexFor } from "../files/session-file-index";
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

const NONE: RecentFilesState = { files: [], loading: false, hasHistory: false };
const projections = new Map<ClientSession, Accessor<RecentFilesState>>();

// One projection per session, shared by every reader: a search reads it per keystroke without rebuilding it.
registerSessionFeature((session) =>
  createRoot((dispose) => {
    projections.set(
      session,
      createMemo(() => project(session)),
    );
    return () => {
      projections.delete(session);
      dispose();
    };
  }),
);

/**
 * `session`'s checkout paths in its repo's frecency order, intersected with its exact file index. The history
 * is checkout-relative, so recency follows a file across the repo's sessions. A closed session has none.
 */
export const recentFilesOf = (session: ClientSession | null): RecentFilesState =>
  (session === null ? undefined : projections.get(session)?.()) ?? NONE;

function project(session: ClientSession): RecentFilesState {
  const history = repoStateOf(session).recentFiles;
  const index = fileIndexFor(session);
  if (index.root === null || index.pending) {
    return { files: [], loading: true, hasHistory: history.length > 0 };
  }
  index.version;
  return {
    files: projectRecentFiles(history, index),
    loading: false,
    hasHistory: history.length > 0,
  };
}
