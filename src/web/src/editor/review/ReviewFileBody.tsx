import { type Accessor, createEffect, createSignal, type JSX, onCleanup, Show } from "solid-js";
import type { ReviewCopy } from "../editor-host";
import type { InlineDiff, ReviewScopeState } from "../inline-diff";
import { createReviewEditor, type ReviewEditor } from "./review-editor";
import type { ReviewFileDiff, ReviewFileView } from "./review-store";
import type { ReviewSectionRegistry } from "./review-surface";

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
  scope: ReviewScopeState;
  active: () => boolean;
  toolbarHost: () => HTMLElement | null;
  configureDiff: (inline: InlineDiff, uri: string, diff: ReviewFileDiff) => void;
  onReveal: () => void;
  onCursor: (line: number) => void;
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
  const [openError, setOpenError] = createSignal("");

  let mount: HTMLDivElement | undefined;
  let live: ReviewEditor | undefined;
  const [mounted, setMounted] = createSignal<ReviewEditor>();
  let liveExists: boolean | undefined;
  let resolution = 0;
  let dropped = false;

  createEffect(() => {
    void props.position;
    mounted()?.layout();
  });

  // The row this body belongs to is keyed by path, so it is fixed for the body's life — and reading it back out
  // of the virtualized <Show> during teardown would be a stale read.
  const path = summary().path;
  const publish = (): void => {
    if (live !== undefined) props.register.set(path, live);
  };
  const disposeEditor = (): void => {
    if (live === undefined) return;
    props.register.clear(path, live);
    live.dispose();
    live = undefined;
    setMounted(undefined);
    liveExists = undefined;
  };
  createEffect(() => {
    props.active();
    mounted()?.inline.refreshPresentation();
  });
  createEffect(() => {
    const editor = mounted();
    const value = diff();
    if (editor !== undefined && value !== null) editor.update(value);
  });
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
        disposeEditor();
        mount?.style.removeProperty("height");
        props.measure();
      }
      return;
    }
    if (live !== undefined && liveExists !== value.currentExists) {
      resolution += 1;
      disposeEditor();
      if (mount !== undefined) {
        mount.style.height = `${props.editorHeight()}px`;
      }
    }
    if (live !== undefined) {
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
          scope: props.scope,
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
          active: props.active,
          toolbarHost: () => (props.active() ? props.toolbarHost() : null),
          configure: props.configureDiff,
          onReveal: props.onReveal,
          onCursor: props.onCursor,
        });
        setMounted(live);
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
    disposeEditor();
  });

  return (
    <>
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
