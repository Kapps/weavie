import { ChevronDown, ChevronRight } from "lucide-solid";
import { type Accessor, createEffect, For, type JSX, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { ReviewCopy } from "../editor-host";
import type { InlineDiff, ReviewScopeState } from "../inline-diff";
import type { TabOwner } from "../tab-owner";
import { ReviewFileBody } from "./ReviewFileBody";
import type { ReviewFileDiff, ReviewFileView } from "./review-store";
import type { ReviewSectionRegistry } from "./review-surface";

export function ReviewFileSection(props: {
  session: ClientSession;
  tab: TabOwner;
  scope: ReviewScopeState;
  displayPath: (path: string) => string;
  file: Accessor<ReviewFileView>;
  scroller: () => HTMLElement;
  editorHeight: () => number;
  onEditorHeight: (height: number) => void;
  index: number;
  measure: (element: HTMLElement) => void;
  onFocus: (line: number) => void;
  active: () => boolean;
  toolbarHost: () => HTMLElement | null;
  configureDiff: (inline: InlineDiff, uri: string, diff: ReviewFileDiff) => void;
  onReveal: () => void;
  openCopy: (diff: ReviewFileDiff) => Promise<ReviewCopy>;
  register: ReviewSectionRegistry;
  style: string;
}): JSX.Element {
  const summary = () => props.file().summary();
  const collapsed = () => props.file().collapsed();
  const pending = () => props.file().pending();
  const bodyId = (): string => `unified-review-file-body-${props.index}`;

  let article: HTMLElement | undefined;
  let header!: HTMLElement;
  const remeasure = (): void => {
    if (article !== undefined) {
      props.measure(article);
    }
  };
  createEffect(() => {
    void collapsed();
    queueMicrotask(remeasure);
  });

  return (
    <article
      class="unified-review-file"
      classList={{ collapsed: collapsed() }}
      data-index={props.index}
      ref={(element) => {
        article = element;
        props.measure(element);
      }}
      onFocusIn={() => {
        if (!props.active()) props.onFocus(summary().line);
      }}
      style={props.style}
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
      <Show when={!collapsed()}>
        <div id={bodyId()}>
          <ReviewFileBody
            session={props.session}
            tab={props.tab}
            position={props.style}
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
            onReveal={props.onReveal}
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
      </Show>
    </article>
  );
}

function ReviewStatus(props: { file: Accessor<ReviewFileView> }): JSX.Element {
  return (
    <span class="unified-review-status">{props.file().loaded() ? "Reviewed" : "Loading…"}</span>
  );
}
