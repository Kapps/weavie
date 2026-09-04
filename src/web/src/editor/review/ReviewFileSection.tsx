import { ChevronDown, ChevronRight } from "lucide-solid";
import { type Accessor, createEffect, createSignal, type JSX, onCleanup, Show } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { ReviewCopy } from "../editor-host";
import { createReviewEditor, estimatedEditorHeight, type ReviewEditor } from "./review-editor";
import type { ReviewFileDiff, ReviewFileView } from "./review-store";
import type { ReviewSection, ReviewSectionRegistry } from "./review-walk";

/** Whether a file still has anything to show: pending changes, or kept ones in its reviewed band. */
function hasChanges(diff: ReviewFileDiff): boolean {
  return (
    diff.baseline !== diff.current ||
    diff.baselineExists !== diff.currentExists ||
    diff.acceptedBaseline !== diff.baseline ||
    diff.acceptedBaselineExists !== diff.baselineExists
  );
}

export function ReviewFileSection(props: {
  displayPath: (path: string) => string;
  file: Accessor<ReviewFileView>;
  index: number;
  measure: (element: HTMLElement) => void;
  onFocus: () => void;
  openCopy: (diff: ReviewFileDiff) => Promise<ReviewCopy>;
  register: ReviewSectionRegistry;
  style: string;
}): JSX.Element {
  const summary = () => props.file().summary();
  const collapsed = () => props.file().collapsed();
  const pending = () => props.file().pending();
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
            file={props.file}
            measure={remeasure}
            openCopy={props.openCopy}
            register={props.register}
          />
        </div>
      </Show>
    </article>
  );
}

function ReviewFileBody(props: {
  file: Accessor<ReviewFileView>;
  measure: () => void;
  openCopy: (diff: ReviewFileDiff) => Promise<ReviewCopy>;
  register: ReviewSectionRegistry;
}): JSX.Element {
  const summary = () => props.file().summary();
  const diff = () => props.file().diff();
  const [diffNotice, setDiffNotice] = createSignal("");
  const [openError, setOpenError] = createSignal("");

  let mount: HTMLDivElement | undefined;
  let live: ReviewEditor | undefined;
  let liveExists: boolean | undefined;
  let resolution = 0;
  let dropped = false;

  // The row this body belongs to is keyed by path, so it is fixed for the body's life — and reading it back out
  // of the virtualized <Show> during teardown would be a stale read.
  const path = summary().path;
  // One handle for the body's whole life, answering from whatever editor is live right now. Published on every
  // paint too, so a walk that arrived before the geometry existed can settle the moment it does.
  const section: ReviewSection = {
    element: () => mount,
    painted: () => live?.painted() ?? false,
    changeLines: () => live?.changeLines() ?? [],
    topForLine: (line) => live?.topForLine(line) ?? 0,
  };
  const publish = (): void => props.register.set(path, section);
  // A file whose diff has landed and holds nothing to show — including one kept all the way through, whose
  // diff is null precisely because it is done.
  const nothingLeft = (): boolean => {
    const value = diff();
    return props.file().loaded() && (value === null || !hasChanges(value));
  };

  createEffect(() => {
    const value = diff();
    if (value === null || !hasChanges(value)) {
      resolution += 1;
      if (live !== undefined) {
        live.dispose();
        live = undefined;
        liveExists = undefined;
        publish();
        mount?.style.removeProperty("height");
        props.measure();
      }
      return;
    }
    if (live !== undefined && liveExists !== value.currentExists) {
      resolution += 1;
      live.dispose();
      live = undefined;
      liveExists = undefined;
      publish();
      if (mount !== undefined) {
        mount.style.height = `${estimatedEditorHeight(summary().added, summary().removed)}px`;
      }
    }
    if (live !== undefined) {
      live.update(value);
      return;
    }
    const token = ++resolution;
    void props.openCopy(value).then(
      (copy) => {
        const latest = diff();
        if (
          dropped ||
          token !== resolution ||
          mount === undefined ||
          latest === null ||
          !hasChanges(latest)
        ) {
          return;
        }
        liveExists = latest.currentExists;
        live = createReviewEditor({
          container: mount,
          model: copy.model,
          editable: copy.editable,
          diff: latest,
          onHeight: props.measure,
          onPainted: publish,
          onStatus: (status) =>
            setDiffNotice(
              status === "ready"
                ? ""
                : status === "timed-out"
                  ? "Diff calculation timed out — the file is shown in full."
                  : "Diff calculation failed — the file is shown in full.",
            ),
        });
        publish();
      },
      (error: unknown) => {
        if (!dropped && token === resolution) {
          setOpenError(String(error));
        }
      },
    );
  });

  onCleanup(() => {
    dropped = true;
    resolution += 1;
    live?.dispose();
    live = undefined;
    props.register.clear(path, section);
  });

  return (
    <>
      <Show when={diffNotice() !== ""}>
        <div class="unified-review-notice">{diffNotice()}</div>
      </Show>
      <Show when={openError() !== ""}>
        <div class="unified-review-notice">Couldn't open this file: {openError()}</div>
      </Show>
      <Show when={!props.file().loaded()}>
        <div class="unified-review-notice">Loading diff…</div>
      </Show>
      <Show when={nothingLeft()}>
        <div class="unified-review-notice">No changes remain in this file.</div>
      </Show>
      <div
        class="unified-review-editor"
        ref={(element) => {
          mount = element;
          element.style.height = `${estimatedEditorHeight(summary().added, summary().removed)}px`;
        }}
      />
    </>
  );
}

function ReviewStatus(props: { file: Accessor<ReviewFileView> }): JSX.Element {
  return (
    <span class="unified-review-status">{props.file().loaded() ? "Reviewed" : "Loading…"}</span>
  );
}
