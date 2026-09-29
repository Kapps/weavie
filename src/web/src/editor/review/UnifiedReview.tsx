import {
  createEffect,
  createMemo,
  createSelector,
  createSignal,
  For,
  type JSX,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import type { ClientSession } from "../../bridge";
import { selectedSession } from "../../bridge";
import type { InteractionIntent } from "../../chrome/interaction-intent";
import {
  buildPathTree,
  type PathTreeNode,
  pathTreeDirectoryKeys,
  visiblePathTreeRows,
} from "../../files/path-tree";
import type { ReviewCopyScope } from "../editor-host";
import { normalizePath, repoRelativePath, samePath } from "../fs-path";
import type { InlineDiffOptions, ReviewScopeState } from "../inline-diff";
import { activeTabFor } from "../session-store";
import { createTabActivity } from "../tab-activity";
import type { TabOwner } from "../tab-owner";
import { ReviewCommentDrafts } from "./ReviewCommentDrafts";
import { ReviewFileSection } from "./ReviewFileSection";
import { ReviewFileTree } from "./ReviewFileTree";
import type { ReviewCommentDrafts as DraftStore } from "./review-comment-drafts";
import { estimatedEditorHeight } from "./review-context";
import type { ReviewDecision, ReviewDecisionCompletion } from "./review-decision";
import { ReviewDocumentScope } from "./review-document";
import { createReviewFileOwners } from "./review-file-owners";
import { reviewHistoryHandlers } from "./review-history-handlers";
import { ReviewHorizontalPositions } from "./review-horizontal-position";
import { createReviewLayout } from "./review-layout";
import { createReviewPreparationQueue } from "./review-preparation-queue";
import { createReviewScroll, type ReviewScroll } from "./review-scroll";
import type { ReviewFile, ReviewFileDiff, ReviewFileView, ReviewOverview } from "./review-store";
import { createReviewSurface, type UnifiedReviewSurface } from "./review-surface";
import { createReviewToolbarPresenter } from "./review-toolbar-presenter";
import { UnifiedReviewHeader } from "./UnifiedReviewHeader";

const SECTION_HEADER_HEIGHT = 42;
const TREE_HEADER_HEIGHT = 42;
const TREE_ROW_HEIGHT = 28;

export function UnifiedReview(props: {
  interaction: InteractionIntent;
  scope: ReviewScopeState;
  overview: () => ReviewOverview;
  drafts: DraftStore;
  session: ClientSession;
  tab: TabOwner;
  changed: () => void;
  onFileCollapsed: (session: ClientSession, path: string, collapsed: boolean) => void;
  bindSurface: (surface: UnifiedReviewSurface) => () => void;
  clear: () => void;
  diffOptions: (
    session: ClientSession,
    diff: ReviewFileDiff,
    reveal: (file: ReviewFile, line: number) => void,
    captureAdvance: (decision: ReviewDecision) => ReviewDecisionCompletion,
  ) => InlineDiffOptions;
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
  const [committedEditor, setCommittedEditor] = createSignal<string>();
  const [viewTop, setViewTop] = createSignal(0);
  const [anchorPath, setAnchorPath] = createSignal<string>();
  // The selected file, else the first file on screen; none while only the file tree is in view.
  const visibleFile = createMemo((): number | undefined => {
    const path = selectedPath();
    if (path !== null) {
      const index = props.overview().files.findIndex((file) => samePath(file.summary().path, path));
      if (index >= 0) return index;
    }
    const owner = scroll();
    if (owner === undefined) return undefined;
    const height = layout.viewportHeight();
    if (height === undefined || height === 0) return undefined;
    const top = viewTop();
    const bottom = top + height;
    const row = rows().find((item) => item.index > 0 && item.start < bottom && item.end > top);
    return row === undefined ? undefined : row.index - 1;
  });
  const isActiveFile = createSelector(visibleFile);
  const setVisibleFile = (index: number): void => {
    setSelectedPath(props.overview().files[index]?.summary().path ?? null);
  };

  const copies = props.createCopyScope();
  const documents = new ReviewDocumentScope();
  const horizontal = new ReviewHorizontalPositions(props.changed);
  onCleanup(() => {
    documents.dispose();
    copies.dispose();
  });
  const preparePassive = createReviewPreparationQueue();

  const displayPath = (path: string): string => {
    const workspace = props.session.state.lsp.current?.workspace;
    return workspace === undefined ? path : repoRelativePath(workspace, path);
  };
  const files = createMemo(() => props.overview().files);
  const label = createMemo(() => props.overview().label);
  const fileOwners = createReviewFileOwners({
    files,
    label,
    documents,
    optionsFor: (value) =>
      props.diffOptions(
        props.session,
        value,
        (target, line) => surface.reveal(target.path, line, props.interaction.begin()),
        (decision) => surface.captureReviewAdvance(value.path, decision),
      ),
    resolve: (value) => copies.open(value.path, value.current, value.currentExists),
  });
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
  const rows = () => layout.rows();
  const orderedKeys = createMemo(
    () => [
      `${sessionKey()}\0tree`,
      ...files().map((file) => `${sessionKey()}\0${file.summary().path}`),
    ],
    undefined,
    {
      equals: (before, after) =>
        before.length === after.length && before.every((key, index) => key === after[index]),
    },
  );
  const itemKey = createMemo(() => {
    const keys = orderedKeys();
    return (index: number) => keys[index]!;
  });
  const retainedRows = createMemo(() =>
    Array.from({ length: files().length + 1 }, (_, index) => index),
  );
  const layout = createReviewLayout(() => ({
    rangeExtractor: retainedRows,
    count: files().length + 1,
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
    getItemKey: itemKey(),
    getScrollElement: () => scroll()?.viewport ?? null,
    observeElementOffset: (_instance, callback) => {
      const owner = scroll()!;
      callback(owner.getScrollTop(), false);
      return owner.onScroll(() => callback(owner.getScrollTop(), false));
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
  }));
  const virtualizer = layout.instance;
  const rowKeys = createMemo(() => rows().map((row) => String(row.key)));
  createEffect(() => sizeVirtualList(layout.totalSize()));

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

  const active = createTabActivity(props.tab);
  const surface = createReviewSurface({
    interaction: props.interaction,
    horizontal,
    active,
    controls: { refresh: () => toolbar.refresh(), captureActions: () => toolbar.captureActions() },
    signal: props.tab.signal,
    clear: props.clear,
    getScrollTop: () => scroll()!.getScrollTop(),
    setScrollTop: (top) => {
      setAnchorPath(undefined);
      scroll()!.setScrollTop(top);
    },
    focus: () => scroller?.focus(),
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
  const summary = createMemo(() => {
    const currentFiles = files();
    const currentLabel = label();
    const index = visibleFile();
    const count = currentFiles.length;
    const reveal = (index: number): void => {
      const file = currentFiles[index]?.summary();
      if (file !== undefined) surface.reveal(file.path, file.line, props.interaction.begin());
    };
    return {
      fileCount: count,
      label: currentLabel,
      stepIn: () => reveal(index ?? 0),
      nextFile: () => reveal(index === undefined ? 0 : (index + 1) % count),
      prevFile: () => reveal(((index ?? 0) - 1 + count) % count),
    };
  });
  const history = reviewHistoryHandlers(props.session, props.interaction.begin, () => {
    const presentation = props.tab.presentation;
    return ({ path, line }, focus) => {
      if (
        !presentation?.signal.aborted &&
        selectedSession() === props.session &&
        activeTabFor(props.session) === props.tab
      )
        surface.reveal(path, line, focus);
    };
  });
  const toolbar = createReviewToolbarPresenter({
    host: () => toolbarHost ?? null,
    active,
    scope: props.scope,
    target: () => {
      const target = surface.target();
      return target.kind !== "none"
        ? target
        : files().length > 0
          ? { kind: "parked", summary: summary() }
          : { kind: "none" };
    },
    reviewPending: () => files().length > 0,
    composerFocused: () => {
      const target = surface.target();
      return target.kind === "file" && target.presentation.composerFocused();
    },
    history: () => props.overview().history,
    historyHandlers: () => history,
  });
  onCleanup(toolbar.dispose);
  createEffect(() => {
    files();
    surface.refresh();
  });
  createEffect(() => onCleanup(props.bindSurface(surface)));
  createEffect(() => {
    visibleFile();
    props.overview().history;
    active();
    toolbar.refresh();
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
      <ReviewCommentDrafts drafts={props.drafts} />

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
                              active={() => isActiveFile(item().index - 1)}
                              requestFocus={() => surface.requestFocus(view().summary().path)}
                              ownsEditor={() =>
                                committedEditor() === normalizePath(view().summary().path)
                              }
                              claimEditor={() => {
                                const path = normalizePath(view().summary().path);
                                setCommittedEditor(path);
                              }}
                              preparePassive={preparePassive}
                              documents={documents}
                              horizontal={horizontal.forPath(view().summary().path)}
                              controlsChanged={toolbar.refresh}
                              openCopy={async () => {
                                const owner = fileOwners().get(view());
                                if (owner === undefined)
                                  throw new DOMException(
                                    "Review file is no longer present",
                                    "AbortError",
                                  );
                                return owner.open();
                              }}
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
                          surface.reveal(
                            file.summary().path,
                            file.summary().line,
                            props.interaction.begin(),
                          )
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
