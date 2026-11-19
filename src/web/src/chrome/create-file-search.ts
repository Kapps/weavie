import { type Accessor, createMemo } from "solid-js";
import type { FileIndex } from "../files/file-index";
import { activeDir, type FileFinder, rankFiles } from "./file-search";

// Reading `version` is what re-runs a search when the list changes; the entries array itself is edited in place.
function finderOf(index: FileIndex): FileFinder {
  index.version;
  return { entries: index.entries };
}

/** Search surfaces show this many ranked rows and disclose the remaining match count. */
export const FILE_SEARCH_VIEW_CAP = 300;

/** Shared fuzzy search over a session's file list, ranking context, and bounded view for file search surfaces. */
export function createFileSearch(options: {
  index: Accessor<FileIndex>;
  query: Accessor<string>;
  recent: Accessor<readonly string[]>;
  currentFile: Accessor<string | null>;
}) {
  // Every listed file as a row. Only the tree view reads it; searching scans the index in place.
  const rows = createMemo(() => {
    const index = options.index();
    index.version;
    return index.entries.map((entry) => entry.row);
  });
  const query = createMemo(() => options.query().trim().replace(/\\/g, "/"));
  const result = createMemo(() =>
    query().length === 0
      ? { matches: [], total: 0 }
      : rankFiles(
          finderOf(options.index()),
          query(),
          options.recent(),
          activeDir(options.currentFile(), options.index().root ?? ""),
        ),
  );
  const view = createMemo(() => result().matches.slice(0, FILE_SEARCH_VIEW_CAP));
  const total = () => result().total;
  const hiddenCount = () => total() - view().length;
  return { rows, query, view, total, hiddenCount };
}
