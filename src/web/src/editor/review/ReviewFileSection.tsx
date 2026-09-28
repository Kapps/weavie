import { ChevronDown, ChevronRight } from "lucide-solid";
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
import type { ReviewScopeState } from "../inline-diff";
import type { TabOwner } from "../tab-owner";
import { ReviewFileBody } from "./ReviewFileBody";
import type { ReviewDocumentScope } from "./review-document";
import type { ReviewEditor } from "./review-editor";
import { createReviewHeaderPosition, measureReviewHeader } from "./review-header-position";
import type { ReviewHorizontalPosition } from "./review-horizontal-position";
import type { ReviewPreparationQueue } from "./review-preparation-queue";
import type { ReviewScroll } from "./review-scroll";
import type { ReviewFileView } from "./review-store";
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
  measure: (element: HTMLElement) => void;
  observe: (element: HTMLElement) => void;
  onFocus: (line: number) => void;
  active: () => boolean;
  activated: () => boolean;
  ownsEditor(): boolean;
  claimEditor(): void;
  preparePassive: ReviewPreparationQueue;
  controlsChanged(): void;
  openCopy: () => Promise<ReviewCopy>;
  register: ReviewSectionRegistry;
  documents: ReviewDocumentScope;
  horizontal: ReviewHorizontalPosition;
  top: number;
}): JSX.Element {
  const summary = () => props.file().summary();
  const collapsed = () => props.file().collapsed();
  const pending = () => props.file().pending();
  const bodyId = (): string => `unified-review-file-body-${props.index}`;

  let article: HTMLElement | undefined;
  let header!: HTMLElement;
  let positionHeader!: ReturnType<typeof createReviewHeaderPosition>;
  let borderTop = 0;
  let headerLimit = 0;
  let placedTop = 0;
  const sectionTop = createMemo(() => props.top);
  const layoutHeader = (): void => {
    positionHeader(props.scroller().getScrollTop(), placedTop, borderTop, headerLimit);
  };
  const measureHeader = (): void => {
    const measured = measureReviewHeader(article!, header);
    borderTop = measured.borderTop;
    headerLimit = measured.limit;
    layoutHeader();
  };
  const [editor, setEditor] = createSignal<Pick<ReviewEditor, "layout" | "shift">>();
  createEffect(() => {
    const top = sectionTop();
    if (article !== undefined && top !== placedTop) {
      untrack(() => {
        const delta = top - placedTop;
        placedTop = top;
        article!.style.top = `${top}px`;
        layoutHeader();
        editor()?.shift(delta);
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
        placedTop = untrack(sectionTop);
        element.style.top = `${placedTop}px`;
        props.observe(element);
      }}
      onFocusIn={() => {
        if (!props.active()) props.onFocus(summary().line);
      }}
    >
      <header
        class="unified-review-file-header"
        ref={(element) => {
          header = element;
          positionHeader = createReviewHeaderPosition(element);
        }}
      >
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
        <Show
          when={summary().currentExists}
          fallback={
            <span class="unified-review-file-name" title="Deleted file — review snapshot">
              {props.displayPath(summary().path)}
            </span>
          }
        >
          <button
            type="button"
            class="unified-review-file-name"
            title={`Open this change in file review${keyHint(CommandIds.reviewOpen)}`}
            onClick={() =>
              void runCommandWithFeedback(CommandIds.reviewOpen, {
                path: summary().path,
                line: summary().line,
              })
            }
          >
            {props.displayPath(summary().path)}
          </button>
        </Show>
        <span class="unified-review-file-stats">
          <span class="unified-review-added">+{summary().added}</span>
          <span class="unified-review-removed">−{summary().removed}</span>
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
      <div id={bodyId()} hidden={collapsed()}>
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
          documents={props.documents}
          horizontal={props.horizontal}
          active={props.active}
          activated={props.activated}
          ownsEditor={props.ownsEditor}
          claimEditor={props.claimEditor}
          preparePassive={props.preparePassive}
          controlsChanged={props.controlsChanged}
          onCursor={props.onFocus}
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
    </article>
  );
}

function ReviewStatus(props: { file: Accessor<ReviewFileView> }): JSX.Element {
  return (
    <span class="unified-review-status">{props.file().loaded() ? "Reviewed" : "Loading…"}</span>
  );
}
