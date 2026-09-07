import { type Accessor, createMemo } from "solid-js";
import { activeDir, createFileFinder, rankFiles, splitPath } from "./file-search";

/** Search surfaces show this many ranked rows and disclose the remaining match count. */
export const FILE_SEARCH_VIEW_CAP = 300;

/** Shared indexed fuzzy search, ranking context, and bounded view for file search surfaces. */
export function createFileSearch(options: {
  files: Accessor<readonly string[]>;
  root: Accessor<string>;
  query: Accessor<string>;
  recent: Accessor<readonly string[]>;
  currentFile: Accessor<string | null>;
}) {
  const rows = createMemo(() => {
    const root = options.root();
    return options.files().map((path) => splitPath(path, root));
  });
  const finder = createMemo(() => createFileFinder(rows()));
  const query = createMemo(() => options.query().trim().replace(/\\/g, "/"));
  const result = createMemo(() =>
    query().length === 0
      ? { matches: [], total: 0 }
      : rankFiles(
          finder(),
          query(),
          options.recent(),
          activeDir(options.currentFile(), options.root()),
        ),
  );
  const view = createMemo(() => result().matches.slice(0, FILE_SEARCH_VIEW_CAP));
  const total = () => result().total;
  const hiddenCount = () => total() - view().length;
  return { rows, query, view, total, hiddenCount };
}
