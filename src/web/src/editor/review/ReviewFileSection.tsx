import { ChevronDown, ChevronRight } from "lucide-solid";
import type { editor as MonacoEditor } from "monaco-editor";
import { type Accessor, createEffect, createSignal, type JSX, onCleanup, Show } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { createReviewEditor, estimatedEditorHeight, type ReviewEditor } from "./review-editor";
import type { ReviewFileDiff, ReviewFileView } from "./review-store";

/** Whether a file still has anything to show: pending changes, or kept ones in its reviewed band. */
function hasChanges(diff: ReviewFileDiff): boolean {
  return diff.baseline !== diff.current || diff.acceptedBaseline !== diff.baseline;
}

export function ReviewFileSection(props: {
  displayPath: (path: string) => string;
  file: Accessor<ReviewFileView>;
  index: number;
  measure: (element: HTMLElement) => void;
  onFocus: () => void;
  openCopy: (path: string) => Promise<MonacoEditor.ITextModel>;
  style: string;
}): JSX.Element {
  const summary = () => props.file().summary();
  const diff = () => props.file().diff();
  const collapsed = () => props.file().collapsed();
  const pending = (): boolean => {
    const value = diff();
    return value !== null && value.baseline !== value.current;
  };
  const bodyId = (): string => `unified-review-file-body-${props.index}`;

  let article: HTMLElement | undefined;
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
      onFocusIn={props.onFocus}
      style={props.style}
    >
      <header class="unified-review-file-header">
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
          <ReviewFileBody file={props.file} measure={remeasure} openCopy={props.openCopy} />
        </div>
      </Show>
    </article>
  );
}

function ReviewFileBody(props: {
  file: Accessor<ReviewFileView>;
  measure: () => void;
  openCopy: (path: string) => Promise<MonacoEditor.ITextModel>;
}): JSX.Element {
  const summary = () => props.file().summary();
  const diff = () => props.file().diff();
  const [diffNotice, setDiffNotice] = createSignal("");
  const [openError, setOpenError] = createSignal("");

  let mount: HTMLDivElement | undefined;
  let live: ReviewEditor | undefined;
  let resolving = false;
  let dropped = false;

  // Mount the file's diff editor once its texts arrive, then keep it painted from every later push. A file whose
  // changes are gone drops the editor and reads as reviewed.
  createEffect(() => {
    const value = diff();
    if (value === null || !hasChanges(value)) {
      if (live !== undefined) {
        live.dispose();
        live = undefined;
        mount?.style.removeProperty("height");
        props.measure();
      }
      return;
    }
    if (live !== undefined) {
      live.update(value);
      return;
    }
    if (resolving) {
      return;
    }
    resolving = true;
    void props.openCopy(value.path).then(
      (model) => {
        resolving = false;
        const latest = diff();
        if (dropped || mount === undefined || latest === null || !hasChanges(latest)) {
          return;
        }
        live = createReviewEditor({
          container: mount,
          model,
          diff: latest,
          onHeight: props.measure,
          onStatus: (status) =>
            setDiffNotice(
              status === "ready"
                ? ""
                : status === "timed-out"
                  ? "Diff calculation timed out — the file is shown in full."
                  : "Diff calculation failed — the file is shown in full.",
            ),
        });
      },
      (error: unknown) => {
        resolving = false;
        setOpenError(String(error));
      },
    );
  });

  onCleanup(() => {
    dropped = true;
    live?.dispose();
  });

  return (
    <>
      <Show when={diffNotice() !== ""}>
        <div class="unified-review-notice">{diffNotice()}</div>
      </Show>
      <Show when={openError() !== ""}>
        <div class="unified-review-notice">Couldn't open this file: {openError()}</div>
      </Show>
      <Show when={diff() === null}>
        <div class="unified-review-notice">Loading diff…</div>
      </Show>
      <Show when={diff() !== null && !hasChanges(diff() as ReviewFileDiff)}>
        <div class="unified-review-notice">No changes remain in this file.</div>
      </Show>
      <div
        class="unified-review-editor"
        ref={(element) => {
          mount = element;
          // Reserve the space the editor will take while its working copy resolves, so the list's offsets
          // don't collapse and re-settle underneath the reader.
          element.style.height = `${estimatedEditorHeight(summary().added, summary().removed)}px`;
        }}
      />
    </>
  );
}

function ReviewStatus(props: { file: Accessor<ReviewFileView> }): JSX.Element {
  return (
    <span class="unified-review-status">
      {props.file().diff() === null ? "Loading…" : "Reviewed"}
    </span>
  );
}
