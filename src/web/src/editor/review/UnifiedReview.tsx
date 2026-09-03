import { createVirtualizer } from "@tanstack/solid-virtual";
import { Check, RotateCcw } from "lucide-solid";
import type { editor as MonacoEditor } from "monaco-editor";
import {
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import {
  buildPathTree,
  type PathTreeNode,
  pathTreeDirectoryKeys,
  visiblePathTreeRows,
} from "../../files/path-tree";
import { normalizePath, repoRelativePath, samePath } from "../fs-path";
import { ReviewFileSection } from "./ReviewFileSection";
import { ReviewFileTree } from "./ReviewFileTree";
import { estimatedEditorHeight } from "./review-editor";
import type { ReviewFileView, ReviewOverview } from "./review-store";

const SECTION_HEADER_HEIGHT = 42;
const TREE_HEADER_HEIGHT = 42;
const TREE_ROW_HEIGHT = 28;

export function UnifiedReview(props: {
  overview: () => ReviewOverview;
  session: ClientSession;
  onCursorChange: (session: ClientSession, path: string, line: number) => void;
  onFileCollapsed: (session: ClientSession, path: string, collapsed: boolean) => void;
  /** Resolve a changed file's working copy for its section editor; released when this surface unmounts. */
  openCopy: (session: ClientSession, path: string) => Promise<MonacoEditor.ITextModel>;
  releaseCopies: () => void;
}): JSX.Element {
  let scroller: HTMLElement | undefined;
  let programmaticSelection = true;
  const initialIndex = (): number => {
    const cursor = props.overview().cursor;
    const index =
      cursor === null
        ? -1
        : props.overview().files.findIndex((file) => samePath(file.summary().path, cursor.path));
    return Math.max(0, index);
  };
  const [visibleFile, setVisibleFile] = createSignal(initialIndex());

  onMount(() => scroller?.focus());
  onCleanup(() => props.releaseCopies());

  const displayPath = (path: string): string => {
    const workspace = props.session.state.lsp.current?.workspace;
    return workspace === undefined ? path : repoRelativePath(workspace, path);
  };
  const files = (): ReviewFileView[] => props.overview().files;
  const sessionKey = (): string =>
    `${props.session.address.slot}\0${props.session.address.incarnation}`;
  const treeNodes = createMemo<PathTreeNode<ReviewFileView>[]>(() =>
    buildPathTree(files().map((file) => ({ path: displayPath(file.summary().path), value: file }))),
  );

  const collapsedDirectories = new WeakMap<ClientSession, Set<string>>();
  const [treeRevision, setTreeRevision] = createSignal(0);
  const collapsedDirectoriesFor = (session: ClientSession): Set<string> => {
    let collapsed = collapsedDirectories.get(session);
    if (collapsed === undefined) {
      collapsed = new Set();
      collapsedDirectories.set(session, collapsed);
    }
    return collapsed;
  };
  const expandedDirectories = createMemo<ReadonlySet<string>>(() => {
    treeRevision();
    const collapsed = collapsedDirectoriesFor(props.session);
    return new Set(pathTreeDirectoryKeys(treeNodes()).filter((key) => !collapsed.has(key)));
  });
  const toggleDirectory = (key: string): void => {
    const collapsed = collapsedDirectoriesFor(props.session);
    if (collapsed.has(key)) {
      collapsed.delete(key);
    } else {
      collapsed.add(key);
    }
    setTreeRevision((revision) => revision + 1);
  };

  const estimatedFileSize = (file: ReviewFileView): number => {
    if (file.collapsed()) {
      return SECTION_HEADER_HEIGHT;
    }
    const summary = file.summary();
    return SECTION_HEADER_HEIGHT + estimatedEditorHeight(summary.added, summary.removed);
  };
  const rows = () => virtualizer.getVirtualItems();
  const rowKeys = (): string[] => rows().map((row) => String(row.key));
  const virtualizer = createVirtualizer<HTMLElement, HTMLElement>({
    get count() {
      return files().length + 1;
    },
    estimateSize: (index) => {
      if (index === 0) {
        const count = visiblePathTreeRows(
          treeNodes(),
          expandedDirectories(),
          Number.POSITIVE_INFINITY,
        ).length;
        return TREE_HEADER_HEIGHT + count * TREE_ROW_HEIGHT;
      }
      const file = files()[index - 1];
      return file === undefined ? 120 : estimatedFileSize(file);
    },
    getItemKey: (index) => {
      if (index === 0) {
        return `${sessionKey()}\0tree`;
      }
      const path = files()[index - 1]?.summary().path;
      return path === undefined ? index : `${sessionKey()}\0${path}`;
    },
    getScrollElement: () => scroller ?? null,
    gap: 20,
    measureElement: (element) => element.getBoundingClientRect().height,
    onChange: (instance) => {
      if (programmaticSelection) {
        return;
      }
      const virtualIndex = instance.range?.startIndex;
      const index = virtualIndex === undefined ? undefined : virtualIndex - 1;
      const summary = index === undefined || index < 0 ? undefined : files()[index]?.summary();
      if (index !== undefined && summary !== undefined) {
        setVisibleFile(index);
        props.onCursorChange(props.session, summary.path, summary.line);
      }
    },
    overscan: 2,
    useAnimationFrameWithResizeObserver: true,
  });

  let collapseSnapshot = new Map<string, boolean>();
  createEffect(() => {
    const next = new Map<string, boolean>();
    files().forEach((file, index) => {
      const key = `${sessionKey()}\0${normalizePath(file.summary().path)}`;
      const collapsed = file.collapsed();
      next.set(key, collapsed);
      const previous = collapseSnapshot.get(key);
      if (previous !== undefined && previous !== collapsed) {
        virtualizer.resizeItem(index + 1, estimatedFileSize(file));
      }
    });
    collapseSnapshot = next;
  });

  let restoredSession: ClientSession | undefined;
  createEffect(() => {
    const session = props.session;
    if (restoredSession === session) {
      return;
    }
    restoredSession = session;
    const index = initialIndex();
    const virtualIndex = props.overview().cursor === null ? 0 : index + 1;
    programmaticSelection = true;
    setVisibleFile(index);
    queueMicrotask(() => {
      if (scroller?.isConnected === true) {
        virtualizer.scrollToIndex(virtualIndex, { align: "start" });
      }
    });
  });

  const setFileCollapsed = (file: ReviewFileView, collapsed: boolean): void => {
    props.onFileCollapsed(props.session, file.summary().path, collapsed);
  };
  const scrollToFile = (file: ReviewFileView): void => {
    const index = files().indexOf(file);
    const summary = file.summary();
    if (index < 0) {
      return;
    }
    setFileCollapsed(file, false);
    programmaticSelection = true;
    setVisibleFile(index);
    props.onCursorChange(props.session, summary.path, summary.line);
    queueMicrotask(() => virtualizer.scrollToIndex(index + 1, { align: "start" }));
  };
  const followViewport = (): void => {
    programmaticSelection = false;
  };
  const measure = (element: HTMLElement): void => {
    queueMicrotask(() => {
      if (element.isConnected) {
        virtualizer.measureElement(element);
      }
    });
  };

  return (
    <section class="unified-review" data-kind="editor" data-review-mode="unified">
      <header class="unified-review-header">
        <div class="unified-review-heading">
          <span class="unified-review-kicker">{props.overview().label || "Review"}</span>
          <strong>Unified review</strong>
        </div>
        <div class="unified-review-header-actions">
          <button
            type="button"
            class="unified-review-action keep"
            disabled={!props.overview().fullyLoaded()}
            title={`Keep all changes${keyHint(CommandIds.keepAll)}`}
            onClick={() => void runCommandWithFeedback(CommandIds.keepAll)}
          >
            <Check size="1em" /> Keep all
          </button>
          <button
            type="button"
            class="unified-review-action revert"
            disabled={!props.overview().fullyLoaded() || !props.overview().hasPending()}
            title={`Revert every pending change${keyHint(CommandIds.undoChange)}`}
            onClick={() => void runCommandWithFeedback(CommandIds.undoChange)}
          >
            <RotateCcw size="1em" /> Revert pending
          </button>
        </div>
      </header>

      <main
        class="unified-review-diffs"
        ref={scroller}
        tabIndex={-1}
        onKeyDown={followViewport}
        onPointerDown={followViewport}
        onWheel={followViewport}
      >
        <div class="unified-review-virtual-list" style={`height:${virtualizer.getTotalSize()}px`}>
          <For each={rowKeys()}>
            {(key) => {
              const row = () => rows().find((candidate) => String(candidate.key) === key);
              const file = () => {
                const index = row()?.index;
                return index === undefined || index === 0 ? undefined : files()[index - 1];
              };
              onCleanup(() => virtualizer.measureElement(null));
              return (
                <Show when={row()}>
                  {(item) => (
                    <Show
                      when={item().index === 0}
                      fallback={
                        <Show when={file()}>
                          {(view) => (
                            <ReviewFileSection
                              displayPath={displayPath}
                              file={view}
                              index={item().index}
                              openCopy={(path) => props.openCopy(props.session, path)}
                              measure={measure}
                              onFocus={() => {
                                const summary = view().summary();
                                setVisibleFile(item().index - 1);
                                props.onCursorChange(props.session, summary.path, summary.line);
                              }}
                              style={`top:${item().start}px`}
                            />
                          )}
                        </Show>
                      }
                    >
                      <ReviewFileTree
                        expanded={expandedDirectories}
                        index={0}
                        measure={measure}
                        nodes={treeNodes}
                        onSelect={scrollToFile}
                        onToggleDirectory={toggleDirectory}
                        overview={props.overview}
                        selectedPath={() => files()[visibleFile()]?.summary().path ?? null}
                        style={`top:${item().start}px`}
                      />
                    </Show>
                  )}
                </Show>
              );
            }}
          </For>
        </div>
      </main>
    </section>
  );
}

export default UnifiedReview;
