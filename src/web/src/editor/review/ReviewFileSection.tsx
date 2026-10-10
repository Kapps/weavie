import {
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ChevronsDownUp,
  ChevronsUpDown,
} from "lucide-solid";
import {
  type Accessor,
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  on,
  onCleanup,
  onMount,
  Show,
  untrack,
} from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { ReviewCopy } from "../editor-host";
import type { InlineDiff, ReviewScopeState } from "../inline-diff";
import type { TabOwner } from "../tab-owner";
import { DiffStats } from "./DiffStats";
import { ReviewFileBody } from "./ReviewFileBody";
import type { ReviewEditor } from "./review-editor";
import type { ReviewScroll } from "./review-scroll";
import {
  isFullContext,
  type LineSpan,
  type ReviewFileDiff,
  type ReviewFileView,
} from "./review-store";
import type { ReviewSectionRegistry } from "./review-surface";

export function ReviewFileSection(props: {
  session: ClientSession;
  tab: TabOwner;
  scope: ReviewScopeState;
  displayPath: (path: string) => string;
  file: Accessor<ReviewFileView>;
  scroller: () => ReviewScroll;
  editorHeight: () => number;
  onEditorHeight: (height: number) => boolean;
  index: number;
  fileCount: number;
  onStep: (delta: 1 | -1) => void;
  onPickFile: (anchor: HTMLElement) => void;
  measure: (element: HTMLElement) => void;
  observe: (element: HTMLElement) => void;
  onFocus: (line: number) => void;
  revealContext: (span: LineSpan) => void;
  active: () => boolean;
  toolbarHost: () => HTMLElement | null;
  configureDiff: (inline: InlineDiff, uri: string, diff: ReviewFileDiff) => void;
  openCopy: (diff: ReviewFileDiff) => Promise<ReviewCopy>;
  register: ReviewSectionRegistry;
  top: number;
}): JSX.Element {
  const summary = () => props.file().summary();
  const collapsed = () => props.file().collapsed();
  const pending = () => props.file().pending();
  const fullContext = () => isFullContext(props.file().context());
  const bodyId = (): string => `unified-review-file-body-${props.index}`;
  const directory = (): string => {
    const path = props.displayPath(summary().path);
    return path.slice(0, path.length - summary().name.length);
  };
  const leaf = (): string => summary().name;

  let article: HTMLElement | undefined;
  let header!: HTMLElement;
  let borderTop = 0;
  let headerLimit = 0;
  const sectionTop = createMemo(() => props.top);
  const layoutHeader = (): void => {
    const offset = Math.max(
      0,
      Math.min(props.scroller().getScrollTop() - sectionTop() - borderTop, headerLimit),
    );
    header.style.transform = `translateY(${offset}px)`;
  };
  const measureHeader = (): void => {
    borderTop = article!.clientTop;
    headerLimit = article!.clientHeight - header.offsetHeight;
  };
  const [editor, setEditor] = createSignal<ReviewEditor>();
  let appliedTop = 0;
  createEffect(() => {
    const top = sectionTop();
    if (article !== undefined) {
      untrack(() => {
        const delta = top - appliedTop;
        appliedTop = top;
        article!.style.top = `${top}px`;
        editor()?.shift(delta);
        layoutHeader();
      });
    }
  });
  const remeasure = (): void => {
    if (article !== undefined) {
      props.measure(article);
    }
  };
  createEffect(on(collapsed, () => queueMicrotask(remeasure), { defer: true }));

  onMount(() => {
    const unsubscribe = props.scroller().onScroll(layoutHeader);
    const observer = new ResizeObserver(() => {
      measureHeader();
      editor()?.layout();
      layoutHeader();
    });
    observer.observe(article!);
    observer.observe(header);
    onCleanup(() => {
      unsubscribe();
      observer.disconnect();
    });
  });

  return (
    <article
      class="unified-review-file"
      classList={{ collapsed: collapsed() }}
      data-index={props.index}
      ref={(element) => {
        article = element;
        props.observe(element);
      }}
      onFocusIn={() => {
        if (!props.active()) props.onFocus(summary().line);
      }}
    >
      <header class="unified-review-file-header" ref={header}>
        <button
          type="button"
          class="unified-review-file-toggle"
          title={`${collapsed() ? "Expand" : "Collapse"} ${props.displayPath(summary().path)}${keyHint(CommandIds.reviewToggleFile)}`}
          aria-controls={bodyId()}
          aria-expanded={!collapsed()}
          onClick={() =>
            void runCommandWithFeedback(CommandIds.reviewToggleFile, { path: summary().path })
          }
        >
          <Show when={collapsed()} fallback={<ChevronDown />}>
            <ChevronRight />
          </Show>
        </button>
        <button
          type="button"
          class="unified-review-file-name"
          title={`Go to file${keyHint(CommandIds.reviewGoToFile)}`}
          aria-haspopup="listbox"
          onClick={(event) => props.onPickFile(event.currentTarget)}
        >
          <span class="unified-review-file-dir">{directory()}</span>
          <span class="unified-review-file-leaf">{leaf()}</span>
          <ChevronDown class="unified-review-file-caret" />
        </button>
        <button
          type="button"
          class="unified-review-file-context"
          title={`${fullContext() ? "Collapse unchanged lines" : "Show full file"}${keyHint(CommandIds.reviewToggleContext)}`}
          aria-pressed={fullContext()}
          onClick={() =>
            void runCommandWithFeedback(CommandIds.reviewToggleContext, { path: summary().path })
          }
        >
          <Show when={fullContext()} fallback={<ChevronsUpDown />}>
            <ChevronsDownUp />
          </Show>
        </button>
        <DiffStats added={summary().added} removed={summary().removed} />
        <span class="unified-review-file-step">
          <button
            type="button"
            title={`Previous file${keyHint(CommandIds.reviewPrevFile)}`}
            onClick={() => props.onStep(-1)}
          >
            <ChevronLeft />
          </button>
          {props.index + 1} / {props.fileCount}
          <button
            type="button"
            title={`Next file${keyHint(CommandIds.reviewNextFile)}`}
            onClick={() => props.onStep(1)}
          >
            <ChevronRight />
          </button>
        </span>
        <Show when={pending()} fallback={<ReviewStatus file={props.file} />}>
          <button
            type="button"
            class="unified-review-file-action keep"
            title={`Keep file${keyHint(CommandIds.keepFile)}`}
            onClick={() =>
              void runCommandWithFeedback(CommandIds.keepFile, { path: summary().path })
            }
          >
            Keep file
          </button>
          <button
            type="button"
            class="unified-review-file-action revert"
            title={`Revert file${keyHint(CommandIds.revertFile)}`}
            onClick={() =>
              void runCommandWithFeedback(CommandIds.revertFile, { path: summary().path })
            }
          >
            Revert file
          </button>
        </Show>
      </header>
      <Show when={!collapsed()}>
        <div id={bodyId()}>
          <ReviewFileBody
            session={props.session}
            tab={props.tab}
            onEditor={setEditor}
            header={() => header}
            scroller={props.scroller}
            editorHeight={props.editorHeight}
            onEditorHeight={props.onEditorHeight}
            scope={props.scope}
            file={props.file}
            measure={remeasure}
            openCopy={props.openCopy}
            register={props.register}
            active={props.active}
            toolbarHost={props.toolbarHost}
            configureDiff={props.configureDiff}
            onCursor={props.onFocus}
            revealContext={props.revealContext}
          />
          <For each={props.file().diff()?.rejected}>
            {(rejected) => (
              <div class="unified-review-rejection">
                <span>
                  Rejected proposal
                  {rejected.stale ? " — changed since rejection; undo unavailable" : ""}
                </span>
                <pre>{rejected.text || "(empty file)"}</pre>
              </div>
            )}
          </For>
        </div>
      </Show>
    </article>
  );
}

function ReviewStatus(props: { file: Accessor<ReviewFileView> }): JSX.Element {
  return (
    <span class="unified-review-status">{props.file().loaded() ? "Reviewed" : "Loading…"}</span>
  );
}
