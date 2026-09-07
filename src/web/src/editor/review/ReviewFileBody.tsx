import { type Accessor, createEffect, createSignal, type JSX, onCleanup, Show } from "solid-js";
import type { ReviewCopy } from "../editor-host";
import { createReviewEditor, type ReviewEditor } from "./review-editor";
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

export function ReviewFileBody(props: {
  position: string;
  header: () => HTMLElement;
  file: Accessor<ReviewFileView>;
  scroller: () => HTMLElement;
  editorHeight: () => number;
  onEditorHeight: (height: number) => void;
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

  createEffect(() => {
    void props.position;
    live?.layout();
  });

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
        mount.style.height = `${props.editorHeight()}px`;
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
          scroller: props.scroller(),
          header: props.header(),
          model: copy.model,
          editable: copy.editable,
          diff: latest,
          onHeight: (height) => {
            props.onEditorHeight(height);
            props.measure();
          },
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
          element.style.height = `${props.editorHeight()}px`;
        }}
      />
    </>
  );
}
