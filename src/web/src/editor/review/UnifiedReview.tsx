import { createVirtualizer } from "@tanstack/solid-virtual";
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
import { selectedSession } from "../../bridge";
import { setContext } from "../../commands/context";
import {
  buildPathTree,
  type PathTreeNode,
  pathTreeDirectoryKeys,
  visiblePathTreeRows,
} from "../../files/path-tree";
import type { ReviewCopyScope } from "../editor-host";
import { normalizePath, repoRelativePath, samePath } from "../fs-path";
import type { InlineDiff, ReviewScopeState } from "../inline-diff";
import { activeTabFor } from "../session-store";
import type { TabOwner } from "../tab-owner";
import { ReviewFileSection } from "./ReviewFileSection";
import { ReviewFileTree } from "./ReviewFileTree";
import { estimatedEditorHeight } from "./review-context";
import { reviewHistoryHandlers } from "./review-history-handlers";
import { createReviewScroll, type ReviewScroll } from "./review-scroll";
import type {
  LineSpan,
  ReviewFile,
  ReviewFileDiff,
  ReviewFileView,
  ReviewOverview,
} from "./review-store";
import { createReviewSurface, type UnifiedReviewSurface } from "./review-surface";
import { createParkedNavigation, createParkedToolbar, mountReviewToolbar } from "./review-toolbar";
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
  onRevealContext: (session: ClientSession, path: string, span: LineSpan) => void;
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
  const [scroll, setScroll] = createSignal<ReviewScroll>();
  const sizeVirtualList = (height: number): void => {
    scroll()?.setContentHeight(height);
  };
  const [selectedPath, setSelectedPath] = createSignal<string | null>(null);
  const [viewTop, setViewTop] = createSignal(0);
  const [anchorPath, setAnchorPath] = createSignal<string>();
  // The selected file, else the first file on screen; none while only the file tree is in view. Memoised because
  // several consumers ask per frame, and it takes the viewport height from the scroll owner rather than the DOM: a
  // read there lands mid-gesture and forces a synchronous layout.
  const visibleFile = createMemo((): number | undefined => {
    const path = selectedPath();
    if (path !== null) {
      const index = props.overview().files.findIndex((file) => samePath(file.summary().path, path));
      if (index >= 0) return index;
    }
    const owner = scroll();
    if (owner === undefined) return undefined;
    const top = viewTop();
    const bottom = top + owner.getViewportHeight();
    const row = rows().find((item) => item.index > 0 && item.start < bottom && item.end > top);
    return row === undefined ? undefined : row.index - 1;
  });
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
    getScrollElement: () => scroll()?.viewport ?? null,
    // Report the gesture truthfully. While the review is scrolling the virtualiser still registers a new section
    // with its ResizeObserver but skips its own synchronous measurement, so the size arrives from that observer's
    // entry instead of a `getBoundingClientRect` that forces a layout mid-gesture. Claiming a settled scroll, as
    // this did before, took the forced read on every section that came into view.
    observeElementOffset: (_instance, callback) => {
      const owner = scroll()!;
      callback(owner.getScrollTop(), false);
      let moved = false;
      let settling = false;
      let stopped = false;
      const settle = (): void => {
        if (stopped) return;
        if (moved) {
          moved = false;
          requestAnimationFrame(settle);
          return;
        }
        settling = false;
        callback(owner.getScrollTop(), false);
      };
      const unsubscribe = owner.onScroll((userInitiated) => {
        // A programmatic scroll arriving while the review is still — a saved-position restore, a Find or
        // go-to-line reveal, the scroll-into-view on focus — must let the virtualiser measure a section as it
        // mounts, or it lands against an estimate. One arriving mid-gesture belongs to that gesture: the
        // virtualiser's own size corrections write the scroll position this way, and flapping the flag per frame
        // would re-render the list on every edge.
        if (!userInitiated && !settling) {
          callback(owner.getScrollTop(), false);
          return;
        }
        moved = true;
        callback(owner.getScrollTop(), true);
        if (settling) return;
        settling = true;
        requestAnimationFrame(settle);
      });
      // The pending frame outlives the subscription, so it has to be stopped too: reporting a settled scroll after
      // teardown would call back into a virtualiser that is already gone.
      return () => {
        stopped = true;
        unsubscribe();
      };
    },
    gap: 20,
    scrollToFn: (offset, options, instance) => {
      const owner = scroll()!;
      const top =
        options.adjustments === undefined ? offset : owner.getScrollTop() + options.adjustments;
      sizeVirtualList(instance.getTotalSize());
      owner.setScrollTop(top);
    },
    // Observer snapshots must apply before navigation, not overwrite newer explicit sizes next frame.
    measureElement: (element, entry, instance) =>
      entry === undefined
        ? (instance.itemSizeCache.get(
            instance.options.getItemKey(instance.indexFromElement(element)),
          ) ?? element.getBoundingClientRect().height)
        : entry.borderBoxSize[0]!.blockSize,
    onChange: (instance) => sizeVirtualList(instance.getTotalSize()),
    overscan: 1,
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
    active: () => selectedSession() === props.session && activeTabFor(props.session) === props.tab,
    signal: props.tab.signal,
    clear: props.clear,
    getScrollTop: () => scroll()!.getScrollTop(),
    setScrollTop: (top) => {
      setAnchorPath(undefined);
      scroll()!.setScrollTop(top);
    },
    focus: () => scroller?.focus(),
    changed,
    files,
    currentIndex: visibleFile,
    select: (index) => {
      setAnchorPath(props.overview().files[index]?.summary().path);
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
    const count = overview.files.length;
    const reveal = (index: number): void => {
      const file = overview.files[index]?.summary();
      if (file !== undefined) surface.reveal(file.path, file.line);
    };
    return {
      fileCount: overview.files.length,
      fileIndex: index + 1,
      label: overview.label,
      stepIn: () => reveal(index ?? 0),
      nextFile: () => reveal(index === undefined ? 0 : (index + 1) % count),
      prevFile: () => reveal(((index ?? 0) - 1 + count) % count),
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
    if (toolbarHost !== undefined) mountReviewToolbar(toolbarHost, controls.bar);
    onCleanup(() => controls.bar.remove());
  });

  // A navigated file holds its place while files above it measure, until the user takes over.
  createEffect(() => {
    const path = anchorPath();
    const index =
      path === undefined ? -1 : files().findIndex((file) => samePath(file.summary().path, path));
    virtualizer.shouldAdjustScrollPositionOnItemSizeChange =
      index < 0 ? undefined : (item) => item.index <= index;
  });
  const followViewport = (): void => {
    setAnchorPath(undefined);
    surface.takeControl();
  };
  onMount(() => {
    const element = scroller!;
    const owner = createReviewScroll(element, virtualList!);
    setScroll(owner);
    sizeVirtualList(virtualizer.getTotalSize());
    const changedScroll = owner.onScroll((userInitiated) => {
      setViewTop(owner.getScrollTop());
      if (userInitiated) {
        setAnchorPath(undefined);
        const row = virtualizer.getVirtualItemForOffset(owner.getScrollTop());
        if (row !== undefined && row.index > 0) setVisibleFile(row.index - 1);
      }
      surface.refresh();
      props.changed();
    });
    onCleanup(() => {
      changedScroll();
      owner.dispose();
    });
    for (const event of ["keydown", "pointerdown", "wheel"]) {
      element.addEventListener(event, followViewport, true);
      onCleanup(() => element.removeEventListener(event, followViewport, true));
    }
  });
  const observe = (element: HTMLElement): void => {
    const commit = (): void => {
      if (element.isConnected) {
        virtualizer.measureElement(element);
      }
    };
    if (element.isConnected) commit();
    else queueMicrotask(commit);
  };
  const measure = (element: HTMLElement): void => {
    if (element.isConnected) {
      virtualizer.resizeItem(Number(element.dataset.index), element.getBoundingClientRect().height);
    }
  };

  return (
    <section class="unified-review" data-kind="editor" data-review-mode="unified">
      <UnifiedReviewHeader overview={props.overview} />

      <main class="unified-review-diffs" ref={scroller} tabIndex={-1}>
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
                              scroller={() => scroll()!}
                              editorHeight={() => editorHeight(view())}
                              onEditorHeight={(height) => {
                                const file = view();
                                if (editorHeights.get(file) === height) return false;
                                editorHeights.set(file, height);
                                return true;
                              }}
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
                              openCopy={(diff) =>
                                copies.open(diff.path, diff.current, diff.currentExists)
                              }
                              revealContext={(span) =>
                                props.onRevealContext(props.session, view().summary().path, span)
                              }
                              measure={measure}
                              observe={observe}
                              onFocus={() => {
                                setVisibleFile(item().index - 1);
                                props.changed();
                              }}
                              top={item().start}
                            />
                          )}
                        </Show>
                      }
                    >
                      <ReviewFileTree
                        expanded={expandedDirectories}
                        index={0}
                        measure={observe}
                        nodes={treeNodes}
                        onSelect={(file) =>
                          surface.reveal(file.summary().path, file.summary().line)
                        }
                        onToggleDirectory={toggleDirectory}
                        overview={props.overview}
                        selectedPath={() => {
                          const index = visibleFile();
                          return index === undefined
                            ? null
                            : (files()[index]?.summary().path ?? null);
                        }}
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
