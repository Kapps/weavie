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
import type { ReviewCopyScope } from "../editor-host";
import { normalizePath, repoRelativePath, samePath } from "../fs-path";
import { ReviewFileSection } from "./ReviewFileSection";
import { ReviewFileTree } from "./ReviewFileTree";
import { estimatedEditorHeight } from "./review-editor";
import type { ReviewFileView, ReviewOverview, UnifiedReviewNavigator } from "./review-store";
import { createReviewWalk } from "./review-walk";
import { UnifiedReviewHeader } from "./UnifiedReviewHeader";

const SECTION_HEADER_HEIGHT = 42;
const TREE_HEADER_HEIGHT = 42;
const TREE_ROW_HEIGHT = 28;

export function UnifiedReview(props: {
  overview: () => ReviewOverview;
  session: ClientSession;
  onCursorChange: (session: ClientSession, path: string, line: number) => void;
  onFileCollapsed: (session: ClientSession, path: string, collapsed: boolean) => void;
  /** Hands this surface's own review walk to the controller for as long as it is mounted. */
  bindNavigator: (session: ClientSession, navigator: UnifiedReviewNavigator) => () => void;
  /** Resolve a changed file's working copy for its section editor; released when this surface unmounts. */
  createCopyScope: () => ReviewCopyScope;
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
        walk.anchor(summary.line);
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

  // The review walk lives beside this surface: it owns the section registry and the stepping, this component
  // owns the virtualizer, the follow-mode flag and the selection it moves.
  const walk = createReviewWalk(
    {
      files,
      currentIndex: visibleFile,
      select: (index, path, line) => {
        programmaticSelection = true;
        setVisibleFile(index);
        props.onCursorChange(props.session, path, line);
      },
      expand: (file) => setFileCollapsed(file, false),
      scroller: () => scroller,
      scrollToIndex: (index) => virtualizer.scrollToIndex(index, { align: "start" }),
    },
    SECTION_HEADER_HEIGHT,
  );
  createEffect(() => onCleanup(props.bindNavigator(props.session, walk)));

  let restoredSession: ClientSession | undefined;
  createEffect(() => {
    const session = props.session;
    if (restoredSession === session) {
      return;
    }
    restoredSession = session;
    const index = initialIndex();
    walk.anchor(props.overview().cursor?.line ?? 0);
    const virtualIndex = props.overview().cursor === null ? 0 : index + 1;
    programmaticSelection = true;
    setVisibleFile(index);
    queueMicrotask(() => {
      if (scroller?.isConnected === true) {
        virtualizer.scrollToIndex(virtualIndex, { align: "start" });
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
                              register={walk.sections}
                              openCopy={(diff) =>
                                copies.open(
                                  props.session,
                                  diff.path,
                                  diff.current,
                                  diff.currentExists,
                                )
                              }
                              measure={measure}
                              onFocus={() => {
                                const summary = view().summary();
                                setVisibleFile(item().index - 1);
                                walk.anchor(summary.line);
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
                        onSelect={walk.goToFile}
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
