import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { type CodeBand, type CodeView, createCodeView } from "../code-view";
import { editorContexts } from "../editor-context";
import { connectTextEditor } from "../editor-contributions";
import {
  createInlineDiff,
  type InlineDiff,
  type InlineDiffPresentation,
  type ReviewScopeState,
} from "../inline-diff";
import { monaco } from "../monaco-setup";
import type { TextLocation } from "../nav-history";
import type { TabOwner } from "../tab-owner";
import type { DiffMarkers } from "./diff-markers";
import { CONTEXT_LINES, collapseUnchanged } from "./review-context";
import { createReviewEditorViewport } from "./review-editor-viewport";
import type { ReviewScroll } from "./review-scroll";
import type { LineSpan, ReviewFileDiff } from "./review-store";

const HIDDEN_AREAS_SOURCE = "weavie.review";

export interface ReviewEditor {
  capture(): TextLocation;
  restore(location: TextLocation): void;
  /** Goes to a line, first showing it with its context when the review folds it away. */
  reveal(location: TextLocation): void;
  revealFileStart(line: number): void;
  focus(): void;
  layout(): void;
  /** Re-applies the collapsed stretches after the file's revealed context changed. */
  refreshContext(): void;
  /** Applies a known change to the section's own offset without re-reading the DOM. */
  shift(delta: number): void;
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
  const horizontalScrollbarSize =
    monaco.editor.EditorOptions.scrollbar.defaultValue.horizontalScrollbarSize;
  let view!: CodeView;
  // The height is the section's once the first diff has laid out its collapsed context.
  let geometryReady = false;
  let height = 0;
  const viewport = createReviewEditorViewport(
    container,
    mount,
    options.scroller,
    options.header,
    (dimension) => {
      view = createCodeView({
        mount,
        // Fixed widgets must escape the transformed scroll content; each editor owns its widget focus.
        widgetsParent: options.scroller.element,
        model,
        dimension,
        overrides: {
          readOnly: !options.editable,
          automaticLayout: false,
          smoothScrolling: false,
          renderLineHighlightOnlyWhenFocus: true,
          // Visible-line width changes must not change the section's height.
          scrollbar: { horizontalScrollbarSize, ignoreHorizontalScrollbarInContentHeight: true },
          padding: { top: 6, bottom: 6 + horizontalScrollbarSize },
        },
        hiddenSource: HIDDEN_AREAS_SOURCE,
        onHeight: measure,
        onClick: (event) => {
          if (options.diff.revision !== null && isLineNumber(event.target))
            void runCommandWithFeedback(CommandIds.reviewOpen, {
              path: options.diff.path,
              line: event.target.position!.lineNumber,
            });
        },
      });
      return view.editor;
    },
  );
  const { editor } = viewport;
  const { widgets } = view;
  // Undefined until InlineDiff first lays out the diff; null for a failed diff.
  let markers: DiffMarkers | null | undefined;
  let hidden: monaco.IRange[] = [];
  let bandAnchor: number | undefined;
  let constructing = true;
  let disposed = false;
  const publish = (): void => {
    if (!disposed) options.onPainted();
  };
  function measure(): void {
    if (!geometryReady) return;
    const next = editor.getContentHeight();
    if (height === next) return;
    height = next;
    container.style.height = `${next}px`;
    viewport.layout();
    options.onHeight(height);
  }
  const revealLine = (line: number): void => {
    const lineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
    viewport.reveal(editor.getTopForLineNumber(line) - (viewport.bounds().height - lineHeight) / 2);
  };
  const band = (span: LineSpan): CodeBand => {
    const count = span.end - span.start + 1;
    const label = `Show ${count} unchanged line${count === 1 ? "" : "s"}`;
    return {
      beforeLine: span.start,
      label,
      title: `${label} — show the whole file${keyHint(CommandIds.reviewToggleContext)}`,
      open: () => {
        // Hold the line beside the band still so the stretch opens in place.
        bandAnchor = span.start === 1 ? span.end + 1 : span.start - 1;
        options.revealContext(span);
        bandAnchor = undefined;
      },
    };
  };
  const applyContext = (): void => {
    if (markers === undefined) return;
    hidden = collapseUnchanged(markers, model.getLineCount(), options.context());
    view.setHidden(
      hidden,
      hidden.map((range) => band({ start: range.startLineNumber, end: range.endLineNumber })),
    );
    geometryReady = true;
    measure();
  };
  const presentation: InlineDiffPresentation = {
    scope: options.scope,
    updateGeometry: viewport.update,
    active: options.active,
    toolbarHost: options.toolbarHost,
    revealLine,
    focus: () => editor.focus(),
    changes: (next) => next.hunks,
    confirmTarget: () => true,
    cleared: () => {},
    toolbarItems: () => [],
    reviewLine: () => {
      const cursor = editor.getPosition()?.lineNumber ?? 1;
      const bounds = viewport.bounds();
      const top = Math.max(0, bounds.top);
      const bottom = bounds.bottom;
      const cursorTop = editor.getTopForLineNumber(cursor);
      // A collapsed line reports the top of the band that replaced it, so it never counts as on screen.
      const collapsed = hidden.some(
        (range) => range.startLineNumber <= cursor && cursor <= range.endLineNumber,
      );
      if (!collapsed && cursorTop >= top && cursorTop < bottom) return cursor;
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
  options.configure(inline, model.uri.toString(), options.diff);
  measure();
  constructing = false;
  container.classList.toggle("navigable", options.diff.revision !== null);
  const subscriptions = [
    editor.onDidChangeCursorPosition((event) => options.onCursor(event.position.lineNumber)),
    editor.onMouseMove((event) => {
      if (options.diff.revision !== null && isLineNumber(event.target))
        event.target.element!.title = "Open file at this line";
    }),
  ];
  return {
    capture,
    restore,
    reveal: (location) => {
      const fold = hidden.find(
        (range) => range.startLineNumber <= location.line && location.line <= range.endLineNumber,
      );
      if (fold !== undefined)
        options.revealContext({
          start: Math.max(fold.startLineNumber, location.line - CONTEXT_LINES),
          end: Math.min(fold.endLineNumber, location.line + CONTEXT_LINES),
        });
      restore(location);
    },
    revealFileStart: (line) => {
      viewport.update(() => editor.setPosition({ lineNumber: line, column: 1 }));
      viewport.reveal(0);
    },
    focus: () => {
      editorContexts.activate(binding.connection);
      editor.focus();
    },
    layout: viewport.layout,
    refreshContext: () => {
      const location = capture();
      if (bandAnchor !== undefined) {
        const offset = viewport.bounds().top - editor.getTopForLineNumber(bandAnchor);
        location.anchor = { line: bandAnchor, offset };
      }
      viewport.update(applyContext);
      restore(location);
    },
    shift: viewport.shift,
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
      view.dispose();
      loading.remove();
      mount.remove();
    },
  };
}

const isLineNumber = (target: monaco.editor.IMouseTarget): boolean =>
  target.type === monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS && target.element !== null;
