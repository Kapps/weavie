import {
  type Accessor,
  batch,
  createEffect,
  createSignal,
  type JSX,
  onCleanup,
  Show,
  untrack,
} from "solid-js";
import type { ClientSession } from "../../bridge";
import type { ReviewCopy } from "../editor-host";
import type { InlineDiff, ReviewScopeState } from "../inline-diff";
import type { TabOwner } from "../tab-owner";
import { createReviewEditor, type ReviewEditor } from "./review-editor";
import type { ReviewSectionGeometry } from "./review-editor-viewport";
import type { ReviewScroll } from "./review-scroll";
import { hasReviewChanges, type ReviewFileDiff, type ReviewFileView } from "./review-store";
import type { ReviewSectionRegistry } from "./review-surface";

export interface ReviewBodyMeasurement {
  observe(element: HTMLElement | undefined): void;
  ready(height: () => number, publish: () => void): void;
}

export function ReviewFileBody(props: {
  session: ClientSession;
  tab: TabOwner;
  onEditor(editor: ReviewEditor | undefined): void;
  header: () => HTMLElement;
  section: ReviewSectionGeometry;
  scope: ReviewScopeState;
  active: () => boolean;
  onToolbar: () => void;
  configureDiff: (inline: InlineDiff, uri: string, diff: ReviewFileDiff) => void;
  onCursor: (line: number) => void;
  file: Accessor<ReviewFileView>;
  scroller: () => ReviewScroll;
  editorHeight: () => number;
  onEditorHeight: (height: number) => void;
  openCopy: (diff: ReviewFileDiff) => Promise<ReviewCopy>;
  register: ReviewSectionRegistry;
  measurement: ReviewBodyMeasurement;
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
  let editorHeight = props.editorHeight();

  // The row this body belongs to is keyed by path, so it is fixed for the body's life — and reading it back out
  // of the virtualized <Show> during teardown would be a stale read.
  const path = summary().path;
  const publish = (): void => {
    props.measurement.ready(
      () => editorHeight,
      () => {
        if (!dropped && live !== undefined) props.register.set(path, live);
      },
    );
  };
  const disposeEditor = (): void => {
    if (live === undefined) return;
    props.register.clear(path, live);
    live.dispose();
    live = undefined;
    setMounted(undefined);
    props.onEditor(undefined);
    liveExists = undefined;
  };
  createEffect(() => {
    props.active();
    const editor = mounted();
    untrack(() => editor?.inline.refreshPresentation());
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
    return props.file().loaded() && (value === null || !hasReviewChanges(value));
  };

  createEffect(() => {
    const value = diff();
    if (value === null || !hasReviewChanges(value)) {
      if (props.file().loaded()) props.register.empty(path);
      resolution += 1;
      if (live !== undefined) {
        disposeEditor();
        mount?.style.removeProperty("height");
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
        if (dropped || token !== resolution || mount === undefined) return;
        const latest = diff();
        if (latest === null || !hasReviewChanges(latest)) return;
        const container = mount;
        batch(() => {
          liveExists = latest.currentExists;
          live = createReviewEditor({
            session: props.session,
            tab: props.tab,
            scope: props.scope,
            container,
            scroller: props.scroller(),
            header: props.header(),
            section: props.section,
            model: copy.model,
            editable: copy.editable,
            path,
            onHeight: (height) => {
              editorHeight = height;
              props.onEditorHeight(height);
            },
            onPainted: publish,
            active: props.active,
            onToolbar: props.onToolbar,
            configure: props.configureDiff,
            onCursor: props.onCursor,
          });
          setMounted(live);
          props.onEditor(live);
        });
      },
      (error: unknown) => {
        if (!dropped && token === resolution) {
          setOpenError(String(error));
          props.register.failed(path, error);
        }
      },
    );
  });

  onCleanup(() => {
    dropped = true;
    resolution += 1;
    props.measurement.observe(undefined);
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
          props.measurement.observe(element);
        }}
      />
    </>
  );
}
