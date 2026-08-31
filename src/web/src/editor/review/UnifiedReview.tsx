import { createVirtualizer } from "@tanstack/solid-virtual";
import { Check, FileCode2, Files, RotateCcw } from "lucide-solid";
import { createEffect, createSignal, For, type JSX, onCleanup, onMount } from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { repoRelativePath, samePath } from "../fs-path";
import { ReviewFileSection } from "./ReviewFileSection";
import type { ReviewFileView, ReviewOverview } from "./review-store";

export function UnifiedReview(props: {
  overview: () => ReviewOverview;
  session: ClientSession;
  onCursorChange: (session: ClientSession, path: string, line: number) => void;
}): JSX.Element {
  let scroller: HTMLElement | undefined;
  let sidebarSelection = true;
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

  const displayPath = (path: string): string => {
    const workspace = props.session.state.lsp.current?.workspace;
    return workspace === undefined ? path : repoRelativePath(workspace, path);
  };
  const files = (): ReviewFileView[] => props.overview().files;
  const virtualizer = createVirtualizer<HTMLElement, HTMLElement>({
    get count() {
      return files().length;
    },
    estimateSize: (index) => {
      const file = files()[index]?.summary();
      return file === undefined ? 120 : 130 + (file.added + file.removed) * 24;
    },
    getItemKey: (index) => files()[index]?.summary().path ?? index,
    getScrollElement: () => scroller ?? null,
    gap: 20,
    measureElement: (element) => element.getBoundingClientRect().height,
    onChange: (instance) => {
      if (sidebarSelection) {
        return;
      }
      const index = instance.range?.startIndex;
      const summary = index === undefined ? undefined : files()[index]?.summary();
      if (index !== undefined && summary !== undefined) {
        setVisibleFile(index);
        props.onCursorChange(props.session, summary.path, summary.line);
      }
    },
    overscan: 2,
    useAnimationFrameWithResizeObserver: true,
  });
  createEffect(() => {
    files().length;
    props.overview().fullyLoaded();
    virtualizer.measure();
  });
  let restoredSession: ClientSession | undefined;
  createEffect(() => {
    const session = props.session;
    if (restoredSession === session) {
      return;
    }
    restoredSession = session;
    const index = initialIndex();
    sidebarSelection = true;
    setVisibleFile(index);
    queueMicrotask(() => {
      if (scroller?.isConnected === true) {
        virtualizer.scrollToIndex(index, { align: "start" });
      }
    });
  });

  const scrollToFile = (index: number): void => {
    const summary = files()[index]?.summary();
    if (summary !== undefined) {
      sidebarSelection = true;
      setVisibleFile(index);
      props.onCursorChange(props.session, summary.path, summary.line);
      virtualizer.scrollToIndex(index, { align: "start" });
    }
  };
  const followViewport = (): void => {
    sidebarSelection = false;
  };

  return (
    <section class="unified-review" data-kind="editor" data-review-mode="unified">
      <header class="unified-review-header">
        <div class="unified-review-heading">
          <span class="unified-review-kicker">{props.overview().label || "Review"}</span>
          <strong>
            {files().length} file{files().length === 1 ? "" : "s"} changed
          </strong>
          <span class="unified-review-totals">
            <span class="unified-review-added">+{props.overview().added}</span>
            <span class="unified-review-removed">−{props.overview().removed}</span>
          </span>
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
          <button
            type="button"
            class="unified-review-action mode"
            title={`Switch to file review${keyHint(CommandIds.reviewToggleMode)}`}
            onClick={() => void runCommandWithFeedback(CommandIds.reviewToggleMode)}
          >
            <FileCode2 size="1em" /> File review
          </button>
        </div>
      </header>

      <div class="unified-review-layout">
        <aside class="unified-review-files" aria-label="Changed files">
          <div class="unified-review-files-title">
            <Files size="1em" /> Files
          </div>
          <For each={files()}>
            {(file, index) => {
              const summary = file.summary;
              return (
                <button
                  type="button"
                  class="unified-review-file-link"
                  classList={{ active: visibleFile() === index() }}
                  title={displayPath(summary().path)}
                  onClick={() => scrollToFile(index())}
                >
                  <span>{displayPath(summary().path)}</span>
                  <small>
                    <span class="unified-review-added">+{summary().added}</span>
                    <span class="unified-review-removed">−{summary().removed}</span>
                  </small>
                </button>
              );
            }}
          </For>
        </aside>

        <main
          class="unified-review-diffs"
          ref={scroller}
          tabIndex={-1}
          onKeyDown={followViewport}
          onPointerDown={followViewport}
          onWheel={followViewport}
        >
          <div class="unified-review-virtual-list" style={`height:${virtualizer.getTotalSize()}px`}>
            <For each={virtualizer.getVirtualItems()}>
              {(row) => {
                const file = (): ReviewFileView => files()[row.index]!;
                onCleanup(() => virtualizer.measureElement(null));
                return (
                  <ReviewFileSection
                    displayPath={displayPath}
                    file={file}
                    index={row.index}
                    measure={(element) =>
                      queueMicrotask(() => {
                        if (element.isConnected) {
                          virtualizer.measureElement(element);
                        }
                      })
                    }
                    style={`transform:translateY(${row.start}px)`}
                  />
                );
              }}
            </For>
          </div>
        </main>
      </div>
    </section>
  );
}

export default UnifiedReview;
