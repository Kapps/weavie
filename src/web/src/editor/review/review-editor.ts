import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { editorContexts } from "../editor-context";
import { connectTextEditor } from "../editor-contributions";
import {
  createInlineDiff,
  type InlineDiff,
  type InlineDiffPresentation,
  type ReviewScopeState,
} from "../inline-diff";
import { createEmbeddedEditor, monaco } from "../monaco-setup";
import type { TextLocation } from "../nav-history";
import type { TabOwner } from "../tab-owner";
import type { DiffMarkers } from "./diff-markers";
import { collapseUnchanged } from "./review-context";
import { createReviewEditorViewport } from "./review-editor-viewport";
import type { ReviewScroll } from "./review-scroll";
import type { LineSpan, ReviewFileDiff } from "./review-store";

const HIDDEN_AREAS_SOURCE = "weavie.review";
const GAP_HEIGHT = 24;
type CollapsingEditor = monaco.editor.IStandaloneCodeEditor & {
  setHiddenAreas(ranges: monaco.IRange[], source: unknown): void;
};

export interface ReviewEditor {
  capture(): TextLocation;
  restore(location: TextLocation): void;
  revealFileStart(line: number): void;
  focus(): void;
  layout(): void;
  /** Re-applies the collapsed stretches after the file's revealed context changed. */
  refreshContext(): void;
  inline: InlineDiff;
  update(diff: ReviewFileDiff): void;
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
  editable: boolean;
  diff: ReviewFileDiff;
  active: () => boolean;
  toolbarHost: () => HTMLElement | null;
  configure: (inline: InlineDiff, uri: string, diff: ReviewFileDiff) => void;
  context: () => readonly LineSpan[];
  revealContext: (span: LineSpan) => void;
  onHeight: (height: number) => void;
  onPainted: () => void;
  onCursor: (line: number) => void;
}): ReviewEditor {
  const { container, model } = options;
  const loading = document.createElement("div");
  loading.className = "unified-review-notice";
  loading.textContent = "Calculating diff…";
  const mount = document.createElement("div");
  mount.className = "unified-review-editor-viewport";
  mount.style.visibility = "hidden";
  container.append(loading, mount);
  // Fixed widgets must escape the transformed scroll content; each editor owns its widget focus.
  const widgets = document.createElement("div");
  widgets.className = "monaco-editor unified-review-overflow-widgets";
  options.scroller.element.append(widgets);
  const horizontalScrollbarSize =
    monaco.editor.EditorOptions.scrollbar.defaultValue.horizontalScrollbarSize;
  const viewport = createReviewEditorViewport(
    container,
    mount,
    options.scroller,
    options.header,
    (dimension) =>
      createEmbeddedEditor(
        mount,
        model,
        { dimension, overflowWidgetsDomNode: widgets },
        {
          readOnly: !options.editable,
          scrollBeyondLastLine: false,
          automaticLayout: false,
          smoothScrolling: false,
          overviewRulerLanes: 0,
          overviewRulerBorder: false,
          hideCursorInOverviewRuler: true,
          minimap: { enabled: false },
          folding: false,
          stickyScroll: { enabled: false },
          renderLineHighlightOnlyWhenFocus: true,
          // Visible-line width changes must not change the section's height.
          scrollbar: { horizontalScrollbarSize, ignoreHorizontalScrollbarInContentHeight: true },
          padding: { top: 6, bottom: 6 + horizontalScrollbarSize },
        },
      ),
  );
  const editor = viewport.editor as CollapsingEditor;
  // Undefined until InlineDiff first lays out the diff; null for a timed-out diff.
  let markers: DiffMarkers | null | undefined;
  let gaps: string[] = [];
  let constructing = true;
  let disposed = false;
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
    viewport.reveal(editor.getTopForLineNumber(line) - (viewport.bounds().height - lineHeight) / 2);
  };
  const gapZone = (range: monaco.IRange): monaco.editor.IViewZone => {
    const span = { start: range.startLineNumber, end: range.endLineNumber };
    const count = span.end - span.start + 1;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "unified-review-gap";
    button.textContent = `Show ${count} unchanged line${count === 1 ? "" : "s"}`;
    button.title = `${button.textContent} — show the whole file${keyHint(CommandIds.reviewToggleContext)}`;
    button.addEventListener("click", () => options.revealContext(span));
    return {
      afterLineNumber: span.start - 1,
      heightInPx: GAP_HEIGHT,
      domNode: button,
      suppressMouseDown: true,
    };
  };
  const applyContext = (): void => {
    if (markers === undefined) return;
    const hidden = collapseUnchanged(markers, model.getLineCount(), options.context());
    editor.setHiddenAreas(hidden, HIDDEN_AREAS_SOURCE);
    editor.changeViewZones((accessor) => {
      for (const id of gaps) accessor.removeZone(id);
      gaps = hidden.map((range) => accessor.addZone(gapZone(range)));
    });
    geometryReady = true;
    measure();
  };
  const presentation: InlineDiffPresentation = {
    scope: options.scope,
    updateGeometry: viewport.update,
    active: options.active,
    toolbarHost: options.toolbarHost,
    revealLine,
    reviewLine: () => {
      const cursor = editor.getPosition()?.lineNumber ?? 1;
      const bounds = viewport.bounds();
      const top = Math.max(0, bounds.top);
      const bottom = bounds.bottom;
      const cursorTop = editor.getTopForLineNumber(cursor);
      if (cursorTop >= top && cursorTop < bottom) return cursor;
      const center = (top + bottom) / 2;
      let first = 1;
      let last = model.getLineCount();
      while (first < last) {
        const middle = Math.ceil((first + last) / 2);
        if (editor.getTopForLineNumber(middle) <= center) first = middle;
        else last = middle - 1;
      }
      return first;
    },
    prepareGeometry: (next) => {
      markers = next;
      applyContext();
    },
    painted: () => {
      if (loading.parentNode !== null) {
        loading.remove();
        editor.render(true);
        mount.style.removeProperty("visibility");
      }
      if (constructing) queueMicrotask(publish);
      else publish();
    },
  };
  const capture = (): TextLocation => {
    const line = presentation.reviewLine();
    return {
      path: options.diff.path,
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
  widgets.addEventListener("focusin", () => {
    editorContexts.activate(binding.connection);
    options.onCursor(editor.getPosition()?.lineNumber ?? 1);
  });
  const inline = createInlineDiff(editor, presentation);
  const contentSize = editor.onDidContentSizeChange(measure);
  options.configure(inline, model.uri.toString(), options.diff);
  measure();
  constructing = false;
  const subscriptions = [
    contentSize,
    editor.onDidChangeCursorPosition((event) => options.onCursor(event.position.lineNumber)),
    editor.onMouseMove((event) => {
      if (options.diff.currentExists && isLineNumber(event.target))
        event.target.element!.title = `Open file at this line${keyHint(CommandIds.reviewOpenLine)}`;
    }),
    editor.onMouseDown((event) => {
      if (options.diff.currentExists && event.event.leftButton && isLineNumber(event.target))
        void runCommandWithFeedback(CommandIds.reviewOpen, {
          path: options.diff.path,
          line: event.target.position!.lineNumber,
        });
    }),
  ];
  return {
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
    refreshContext: () => viewport.update(applyContext),
    inline,
    update: (diff) => options.configure(inline, model.uri.toString(), diff),
    dispose: () => {
      disposed = true;
      if (container.contains(document.activeElement) || widgets.contains(document.activeElement)) {
        options.scroller.element.focus({ preventScroll: true });
      }
      binding.dispose();
      for (const subscription of subscriptions) subscription.dispose();
      viewport.dispose();
      inline.dispose();
      editor.dispose();
      widgets.remove();
      loading.remove();
      mount.remove();
    },
  };
}

const isLineNumber = (target: monaco.editor.IMouseTarget): boolean =>
  target.type === monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS && target.element !== null;
