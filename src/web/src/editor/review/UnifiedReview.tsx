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
import { ReviewFileSection } from "./ReviewFileSection";
import { ReviewFileTree } from "./ReviewFileTree";
import { estimatedEditorHeight } from "./review-context";
import type { ReviewFileDiff, ReviewFileView, ReviewOverview } from "./review-store";
import { createReviewSurface, type UnifiedReviewSurface } from "./review-surface";
import { UnifiedReviewHeader } from "./UnifiedReviewHeader";

const SECTION_HEADER_HEIGHT = 42;
const TREE_HEADER_HEIGHT = 42;
const TREE_ROW_HEIGHT = 28;

export function UnifiedReview(props: {
  scope: ReviewScopeState;
  overview: () => ReviewOverview;
  session: ClientSession;
  onCursorChange: (session: ClientSession, path: string, line: number) => void;
  onFileCollapsed: (session: ClientSession, path: string, collapsed: boolean) => void;
  bindSurface: (session: ClientSession, surface: UnifiedReviewSurface) => () => void;
  refreshControls: () => void;
  configureDiff: (
    session: ClientSession,
    inline: InlineDiff,
    uri: string,
    diff: ReviewFileDiff,
  ) => void;
  /** Resolve a changed file's working copy for its section editor; released when this surface unmounts. */
  createCopyScope: () => ReviewCopyScope;
}): JSX.Element {
  let scroller: HTMLElement | undefined;
  let toolbarHost: HTMLElement | undefined;
  let programmaticSelection = true;
  const initialIndex = (): number => {
    const cursor = props.overview().cursor;
    const index =
      cursor === null
        ? -1
        : props.overview().files.findIndex((file) => samePath(file.summary().path, cursor.path));
    return Math.max(0, index);
  };
  const [selectedPath, setSelectedPath] = createSignal(
    props.overview().files[initialIndex()]?.summary().path ?? null,
  );
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

  onMount(() => scroller?.focus());
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
    scrollToFn: scrollVirtualElement,
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

  const setFileCollapsed = (file: ReviewFileView, collapsed: boolean): void => {
    props.onFileCollapsed(props.session, file.summary().path, collapsed);
  };

  const surface = createReviewSurface({
    focus: () => scroller?.focus(),
    toolbarHost: () => toolbarHost ?? null,
    changed: props.refreshControls,
    files,
    currentIndex: visibleFile,
    select: (index, path, line) => {
      programmaticSelection = true;
      setVisibleFile(index);
      props.onCursorChange(props.session, path, line);
    },
    expand: (file) => setFileCollapsed(file, false),
    scrollToIndex: (index) => virtualizer.scrollToIndex(index, { align: "start" }),
  });
  onCleanup(() => surface.dispose());
  createEffect(() => onCleanup(props.bindSurface(props.session, surface)));
  createEffect(() => {
    visibleFile();
    props.refreshControls();
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
        const cursor = props.overview().cursor;
        if (cursor === null || files()[index]?.collapsed())
          virtualizer.scrollToIndex(virtualIndex, { align: "start" });
        else surface.reveal(cursor.path, cursor.line);
      }
    });
  });

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
      <UnifiedReviewHeader overview={props.overview} />

      <main
        class="unified-review-diffs"
        ref={scroller}
        tabIndex={-1}
        onKeyDown={followViewport}
        onPointerDown={followViewport}
        onWheel={followViewport}
        onScroll={() => surface.refresh()}
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
                              session={props.session}
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
                                props.configureDiff(props.session, inline, uri, diff)
                              }
                              onReveal={() => {
                                programmaticSelection = true;
                              }}
                              openCopy={(diff) =>
                                copies.open(
                                  props.session,
                                  diff.path,
                                  diff.current,
                                  diff.currentExists,
                                )
                              }
                              measure={measure}
                              onFocus={(line) => {
                                const summary = view().summary();
                                setVisibleFile(item().index - 1);
                                props.onCursorChange(props.session, summary.path, line);
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
