import { createVirtualizer } from "@tanstack/solid-virtual";
import {
  batch,
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  onCleanup,
  Show,
} from "solid-js";
import type { ClientSession } from "../../bridge";
import { selectedSession } from "../../bridge";
import { setContext } from "../../commands/context";
import {
  buildPathTree,
  type PathTreeNode,
  pathTreeDirectoryKeys,
  visiblePathTreeRows,
} from "../../files/path-tree";
import { scrollVirtualElement } from "../../virtual-scroll";
import type { ReviewCopyScope } from "../editor-host";
import { normalizePath, repoRelativePath, samePath } from "../fs-path";
import type { InlineDiff, ReviewScopeState } from "../inline-diff";
import { activeTabFor } from "../session-store";
import type { TabOwner } from "../tab-owner";
import { ReviewFileSection } from "./ReviewFileSection";
import { ReviewFileTree } from "./ReviewFileTree";
import { estimatedEditorHeight } from "./review-context";
import { reviewHistoryHandlers } from "./review-history-handlers";
import type { ReviewFile, ReviewFileDiff, ReviewFileView, ReviewOverview } from "./review-store";
import { createReviewSurface, type UnifiedReviewSurface } from "./review-surface";
import { createParkedNavigation, createParkedToolbar } from "./review-toolbar";
import { UnifiedReviewHeader } from "./UnifiedReviewHeader";

const SECTION_HEADER_HEIGHT = 42;
const TREE_HEADER_HEIGHT = 42;
const TREE_ROW_HEIGHT = 28;

export function UnifiedReview(props: {
  scope: ReviewScopeState;
  overview: () => ReviewOverview;
  session: ClientSession;
  tab: TabOwner;
  changed: () => void;
  onFileCollapsed: (session: ClientSession, path: string, collapsed: boolean) => void;
  bindSurface: (surface: UnifiedReviewSurface) => () => void;
  clear: () => void;
  configureDiff: (
    tab: TabOwner,
    inline: InlineDiff,
    uri: string,
    diff: ReviewFileDiff,
    reveal: (file: ReviewFile, line: number) => void,
  ) => void;
  /** Resolve a changed file's working copy for its section editor; released when this surface unmounts. */
  createCopyScope: () => ReviewCopyScope;
}): JSX.Element {
  let scroller: HTMLElement | undefined;
  let virtualList: HTMLDivElement | undefined;
  let toolbarHost: HTMLElement | undefined;
  const sizeVirtualList = (height: number): void => {
    if (virtualList !== undefined) virtualList.style.height = `${height}px`;
  };
  let programmaticSelection = true;
  const [selectedPath, setSelectedPath] = createSignal<string | null>(null);
  const visibleFile = (): number =>
    Math.max(
      0,
      props
        .overview()
        .files.findIndex(
          (file) => selectedPath() !== null && samePath(file.summary().path, selectedPath()!),
        ),
    );
  const setVisibleFile = (index: number): void => {
    setSelectedPath(props.overview().files[index]?.summary().path ?? null);
  };

  const copies = props.createCopyScope();
  onCleanup(() => copies.dispose());

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

  const collapsedDirectories = new Set<string>();
  const [treeRevision, setTreeRevision] = createSignal(0);
  const expandedDirectories = createMemo<ReadonlySet<string>>(() => {
    treeRevision();
    const collapsed = collapsedDirectories;
    return new Set(pathTreeDirectoryKeys(treeNodes()).filter((key) => !collapsed.has(key)));
  });
  const toggleDirectory = (key: string): void => {
    const collapsed = collapsedDirectories;
    if (collapsed.has(key)) {
      collapsed.delete(key);
    } else {
      collapsed.add(key);
    }
    setTreeRevision((revision) => revision + 1);
  };

  const editorHeights = new WeakMap<ReviewFileView, number>();
  const editorHeight = (file: ReviewFileView): number =>
    editorHeights.get(file) ?? estimatedEditorHeight(file.summary().added, file.summary().removed);
  const estimatedFileSize = (file: ReviewFileView): number => {
    if (file.collapsed()) {
      return SECTION_HEADER_HEIGHT;
    }
    return SECTION_HEADER_HEIGHT + editorHeight(file);
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
    scrollToFn: (offset, options, instance) => {
      // TanStack requests resize corrections before notifying Solid of the new scroll range.
      return scrollVirtualElement(offset, options, instance, () =>
        sizeVirtualList(instance.getTotalSize()),
      );
    },
    measureElement: (element) => element.getBoundingClientRect().height,
    onChange: (instance) => {
      sizeVirtualList(instance.getTotalSize());
      if (programmaticSelection) {
        return;
      }
      const virtualIndex = instance.range?.startIndex;
      const index = virtualIndex === undefined ? undefined : virtualIndex - 1;
      const summary = index === undefined || index < 0 ? undefined : files()[index]?.summary();
      if (index !== undefined && summary !== undefined) {
        setVisibleFile(index);
        props.changed();
      }
    },
    overscan: 2,
    useAnimationFrameWithResizeObserver: true,
  });
  createEffect(() => sizeVirtualList(virtualizer.getTotalSize()));

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

  const setFileCollapsed = (file: ReviewFileView, collapsed: boolean): void => {
    props.onFileCollapsed(props.session, file.summary().path, collapsed);
  };

  const [controlsRevision, setControlsRevision] = createSignal(0);
  const changed = (): void => {
    setControlsRevision((value) => value + 1);
    props.changed();
  };
  const surface = createReviewSurface({
    signal: props.tab.signal,
    clear: props.clear,
    scroller: () => scroller!,
    focus: () => scroller?.focus(),
    changed,
    files,
    currentIndex: visibleFile,
    select: (index) => {
      programmaticSelection = true;
      setVisibleFile(index);
      props.changed();
    },
    expand: (file) => setFileCollapsed(file, false),
    scrollToIndex: (index) => virtualizer.scrollToIndex(index, { align: "start" }),
  });
  onCleanup(() => surface.dispose());
  createEffect(() => {
    files();
    surface.refresh();
  });
  const summary = () => {
    const overview = props.overview();
    const index = visibleFile();
    const reveal = (index: number): void => {
      const file = overview.files[index]?.summary();
      if (file !== undefined) surface.reveal(file.path, file.line);
    };
    return {
      fileCount: overview.files.length,
      label: overview.label,
      stepIn: () => reveal(index),
      nextFile: () => reveal((index + 1) % overview.files.length),
      prevFile: () => reveal((index - 1 + overview.files.length) % overview.files.length),
    };
  };
  const history = reviewHistoryHandlers(props.session, () => {
    const presentation = props.tab.presentation;
    return ({ path, line }) => {
      if (
        !presentation?.signal.aborted &&
        selectedSession() === props.session &&
        activeTabFor(props.session) === props.tab
      )
        surface.reveal(path, line);
    };
  });
  const parkedActions = () =>
    files().length === 0
      ? undefined
      : {
          ...createParkedNavigation(summary()),
          undoKeep: () => {
            history.onUndoKeep();
            return true;
          },
          undoRevert: () => {
            history.onUndoRevert();
            return true;
          },
          redoReview: () => {
            history.onRedo();
            return true;
          },
        };
  createEffect(() =>
    onCleanup(
      props.bindSurface({
        ...surface,
        actions: () => surface.actions() ?? parkedActions(),
      }),
    ),
  );
  createEffect(() => {
    controlsRevision();
    visibleFile();
    const overview = props.overview();
    setContext("diffActive", overview.files.length > 0);
    if (surface.actions() !== undefined || overview.files.length === 0) return;
    const controls = createParkedToolbar(
      summary(),
      {
        ...summary(),
        undo: history.onUndoLast,
        redo: history.onRedo,
      },
      overview.history,
    );
    toolbarHost?.appendChild(controls.bar);
    onCleanup(() => controls.bar.remove());
  });

  const followViewport = (): void => {
    programmaticSelection = false;
  };
  const measure = (element: HTMLElement): void => {
    const commit = (): void => {
      if (element.isConnected) {
        const index = virtualizer.indexFromElement(element);
        const height = element.getBoundingClientRect().height;
        batch(() => {
          virtualizer.measureElement(element);
          virtualizer.resizeItem(index, height);
        });
      }
    };
    if (element.isConnected) commit();
    else queueMicrotask(commit);
  };

  return (
    <section class="unified-review" data-kind="editor" data-review-mode="unified">
      <UnifiedReviewHeader overview={props.overview} />

      <main
        class="unified-review-diffs"
        ref={scroller}
        tabIndex={-1}
        onKeyDown={followViewport}
        onPointerDown={followViewport}
        onWheel={followViewport}
        onScroll={() => {
          surface.refresh();
          props.changed();
        }}
      >
        <div
          class="unified-review-virtual-list"
          ref={(element) => {
            virtualList = element;
            sizeVirtualList(virtualizer.getTotalSize());
          }}
        >
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
                              session={props.session}
                              tab={props.tab}
                              scroller={() => scroller!}
                              editorHeight={() => editorHeight(view())}
                              onEditorHeight={(height) => editorHeights.set(view(), height)}
                              scope={props.scope}
                              displayPath={displayPath}
                              file={view}
                              index={item().index}
                              register={surface.sections}
                              active={() => visibleFile() === item().index - 1}
                              toolbarHost={() => toolbarHost ?? null}
                              configureDiff={(inline, uri, diff) =>
                                props.configureDiff(props.tab, inline, uri, diff, (file, line) =>
                                  surface.reveal(file.path, line),
                                )
                              }
                              onReveal={() => {
                                programmaticSelection = true;
                              }}
                              openCopy={(diff) =>
                                copies.open(diff.path, diff.current, diff.currentExists)
                              }
                              measure={measure}
                              onFocus={() => {
                                setVisibleFile(item().index - 1);
                                props.changed();
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
                        onSelect={(file) =>
                          surface.reveal(file.summary().path, file.summary().line)
                        }
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
      <footer class="unified-review-controls" ref={toolbarHost} />
    </section>
  );
}

export default UnifiedReview;
