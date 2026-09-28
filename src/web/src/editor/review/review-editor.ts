import {
  DisposableStore,
  dispose,
  type IDisposable,
  toDisposable,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import type { ClientSession } from "../../bridge";
import { editorContexts } from "../editor-context";
import { connectTextEditor } from "../editor-contributions";
import {
  createReviewDiffPaint,
  type InlineDiffPaintPresentation,
  type ReviewScopeState,
} from "../inline-diff";
import { createEmbeddedEditor, monaco } from "../monaco-setup";
import type { TextLocation } from "../nav-history";
import type { TabOwner } from "../tab-owner";
import type { ReviewCommentLayout } from "./review-comment-layout";
import { collapseUnchanged } from "./review-context";
import type { ReviewDocumentScope } from "./review-document";
import { createReviewEditorActivity } from "./review-editor-activity";
import { createReviewEditorHorizontal } from "./review-editor-horizontal";
import { reviewEditorOptions } from "./review-editor-options";
import { createReviewEditorViewport } from "./review-editor-viewport";
import type { ReviewHorizontalPosition } from "./review-horizontal-position";
import { reviewLineAtOffset } from "./review-line-geometry";
import { beginExternalMouseDown } from "./review-mouse-handoff";
import type { ReviewScroll } from "./review-scroll";
import type { ReviewSection } from "./review-section";
import type { ReviewToolbarTarget } from "./review-toolbar-state";

const HIDDEN_AREAS_SOURCE = "weavie.review";
type CollapsingEditor = monaco.editor.IStandaloneCodeEditor & {
  setHiddenAreas(ranges: monaco.IRange[], source: unknown): void;
};

export interface ReviewEditor extends ReviewSection {
  layout(): void;
  shift(delta: number): void;
  retained(): boolean;
  ready(): boolean;
  viewState(): monaco.editor.ICodeEditorViewState | null;
  restoreViewState(state: monaco.editor.ICodeEditorViewState): void;
  selectLine(line: number): void;
  beginMouseDown(event: MouseEvent, pointerId: number): void;
  dispose(): void;
}

/** The section owns sizing and collapsed context; InlineDiff owns all review rendering and actions. */
export function createReviewEditor(options: {
  session: ClientSession;
  tab: TabOwner;
  scope: ReviewScopeState;
  container: HTMLElement;
  scroller: ReviewScroll;
  header: HTMLElement;
  model: monaco.editor.ITextModel;
  documents: ReviewDocumentScope;
  comments: ReviewCommentLayout;
  horizontal: ReviewHorizontalPosition;
  preparedWidth: { minimumContentWidth: number; viewportWrapping: boolean };
  editable: boolean;
  path: string;
  active: () => boolean;
  controlsChanged(): void;
  onHeight: (height: number) => void;
  onPainted: () => void;
  onCursor: (line: number) => void;
}): ReviewEditor {
  const { container, model } = options;
  const cleanup: IDisposable[] = [];
  const observers = new DisposableStore();
  let disposed = false;
  let painted = false;
  const release = (): void => {
    if (disposed) return;
    disposed = true;
    dispose([observers, ...cleanup.splice(0).reverse()]);
  };
  try {
    const loading = document.createElement("div");
    cleanup.push(toDisposable(() => loading.remove()));
    loading.className = "unified-review-notice";
    loading.textContent = "Calculating diff…";
    const mount = document.createElement("div");
    cleanup.push(toDisposable(() => mount.remove()));
    mount.className = "unified-review-editor-viewport";
    mount.style.visibility = "hidden";
    container.append(loading, mount);
    const widgets = document.createElement("div");
    cleanup.push(toDisposable(() => widgets.remove()));
    widgets.className = "monaco-editor unified-review-overflow-widgets";
    options.scroller.element.append(widgets);
    const viewport = createReviewEditorViewport(
      container,
      mount,
      widgets,
      options.scroller,
      options.header,
      (dimension) =>
        createEmbeddedEditor(
          mount,
          model,
          { dimension, overflowWidgetsDomNode: widgets },
          {
            ...reviewEditorOptions(),
            readOnly: !options.editable,
          },
        ),
    );
    const editor = viewport.editor as CollapsingEditor;
    cleanup.push(editor);
    observers.add(viewport);
    const horizontal = observers.add(
      createReviewEditorHorizontal(
        editor,
        viewport.update,
        options.horizontal,
        options.preparedWidth,
      ),
    );
    const gaps = editor.createDecorationsCollection([]);
    cleanup.push(toDisposable(() => gaps.clear()));
    let constructing = true;
    const publish = (): void => {
      if (!disposed) options.onPainted();
    };
    let geometryReady = false;
    let height = 0;
    const measure = (): void => {
      if (!geometryReady) return;
      const next = editor.getContentHeight();
      if (height === next) return;
      height = next;
      container.style.height = `${next}px`;
      viewport.layout();
      options.onHeight(height);
    };
    const revealLine = (line: number): void => {
      const lineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
      viewport.reveal(
        editor.getTopForLineNumber(line) - (viewport.bounds().height - lineHeight) / 2,
      );
    };
    const presentation: InlineDiffPaintPresentation = {
      scope: options.scope,
      updateGeometry: viewport.update,
      revealLine,
      reviewLine: () => {
        const cursor = editor.getPosition()?.lineNumber ?? 1;
        const bounds = viewport.bounds();
        const top = Math.max(0, bounds.top);
        const bottom = bounds.bottom;
        const cursorTop = editor.getTopForLineNumber(cursor);
        if (cursorTop >= top && cursorTop < bottom) return cursor;
        return reviewLineAtOffset(
          model.getLineCount(),
          (line) => editor.getTopForLineNumber(line),
          (top + bottom) / 2,
        );
      },
      prepareGeometry: (markers) => {
        const collapsed = collapseUnchanged(markers, model.getLineCount());
        gaps.set(collapsed.gapMarkers);
        editor.setHiddenAreas(collapsed.hidden, HIDDEN_AREAS_SOURCE);
        geometryReady = true;
        measure();
      },
      painted: () => {
        if (loading.parentNode !== null) {
          loading.remove();
          editor.render(true);
          mount.style.removeProperty("visibility");
        }
        painted = true;
        if (constructing) queueMicrotask(publish);
        else publish();
      },
    };
    const capture = (): TextLocation => {
      const line = presentation.reviewLine();
      return {
        path: options.path,
        line,
        viewState: editor.saveViewState(),
        anchor: {
          line,
          offset: viewport.bounds().top - editor.getTopForLineNumber(line),
        },
      };
    };
    const restore = (location: TextLocation): void => {
      viewport.update(() => {
        if (location.viewState != null) editor.restoreViewState(location.viewState);
        else editor.setPosition({ lineNumber: location.line, column: 1 });
      });
      const anchor = location.anchor;
      if (anchor === undefined) revealLine(location.line);
      else viewport.reveal(editor.getTopForLineNumber(anchor.line) + anchor.offset);
    };
    const binding = connectTextEditor({
      session: options.session,
      tab: options.tab,
      editor,
      model,
      capture,
      restore,
    });
    observers.add(binding);
    const widgetFocus = (): void => {
      if (disposed) return;
      editorContexts.activate(binding.connection);
      options.onCursor(editor.getPosition()?.lineNumber ?? 1);
    };
    widgets.addEventListener("focusin", widgetFocus);
    observers.add(toDisposable(() => widgets.removeEventListener("focusin", widgetFocus)));
    let target: ReviewToolbarTarget = { kind: "none" };
    const paint = createReviewDiffPaint(
      editor,
      presentation,
      options.documents,
      options.comments.presenter,
      (value) => {
        target = value;
        if (!disposed && options.active()) options.controlsChanged();
      },
    );
    cleanup.push(paint);
    observers.add(options.comments.bind(editor, viewport.update));
    const activity = createReviewEditorActivity(editor, widgets);
    observers.add(activity);
    observers.add(editor.onDidContentSizeChange(measure));
    observers.add(
      editor.onDidChangeCursorPosition((event) => options.onCursor(event.position.lineNumber)),
    );
    cleanup.push(
      toDisposable(() => {
        if (mount.contains(document.activeElement) || widgets.contains(document.activeElement)) {
          options.scroller.element.focus({ preventScroll: true });
        }
      }),
    );
    measure();
    horizontal.restore(() => {});
    constructing = false;
    return {
      ready: () => painted && !disposed,
      retained: activity.retained,
      viewState: () => editor.saveViewState(),
      restoreViewState: (state) => {
        horizontal.restore(() => editor.restoreViewState(state));
      },
      selectLine: (line) => {
        viewport.update(() => editor.setPosition({ lineNumber: line, column: 1 }));
        editor.render(true);
      },
      beginMouseDown: (event, pointerId) => {
        const target = document.elementFromPoint(event.clientX, event.clientY);
        if (!(target instanceof HTMLElement))
          throw new Error("The original mouse event has no live editor target");
        beginExternalMouseDown(editor, event, pointerId, target);
      },
      capture,
      restore,
      revealFileStart: (line) => {
        viewport.update(() => editor.setPosition({ lineNumber: line, column: 1 }));
        viewport.reveal(0);
      },
      focus: () => {
        editorContexts.activate(binding.connection);
        editor.focus();
      },
      layout: viewport.layout,
      shift: viewport.shift,
      target: () => target,
      dispose: release,
    };
  } catch (error) {
    try {
      release();
    } catch (cleanupError) {
      throw new AggregateError(
        [error, cleanupError],
        "Review editor construction and cleanup failed",
      );
    }
    throw error;
  }
}
