import type { ClientSession } from "../../bridge";
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
import { collapseUnchanged } from "./review-context";
import { createReviewEditorViewport } from "./review-editor-viewport";
import type { ReviewFileDiff } from "./review-store";

const HIDDEN_AREAS_SOURCE = "weavie.review";
type CollapsingEditor = monaco.editor.IStandaloneCodeEditor & {
  setHiddenAreas(ranges: monaco.IRange[], source: unknown): void;
};

export interface ReviewEditor {
  capture(): TextLocation;
  restore(location: TextLocation): void;
  focus(): void;
  layout(): void;
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
  scroller: HTMLElement;
  header: HTMLElement;
  model: monaco.editor.ITextModel;
  editable: boolean;
  diff: ReviewFileDiff;
  active: () => boolean;
  toolbarHost: () => HTMLElement | null;
  onReveal: () => void;
  configure: (inline: InlineDiff, uri: string, diff: ReviewFileDiff) => void;
  onHeight: (height: number) => void;
  onPainted: () => void;
  onCursor: (line: number) => void;
}): ReviewEditor {
  const { container, model } = options;
  const mount = document.createElement("div");
  mount.className = "unified-review-editor-viewport";
  container.appendChild(mount);
  const editor = createEmbeddedEditor(mount, model, {
    readOnly: !options.editable,
    scrollBeyondLastLine: false,
    automaticLayout: false,
    smoothScrolling: false,
    scrollbar: { handleMouseWheel: false, vertical: "hidden" },
    overviewRulerLanes: 0,
    overviewRulerBorder: false,
    hideCursorInOverviewRuler: true,
    minimap: { enabled: false },
    folding: false,
    stickyScroll: { enabled: false },
    renderLineHighlightOnlyWhenFocus: true,
    padding: { top: 6, bottom: 6 },
  }) as CollapsingEditor;
  const viewport = createReviewEditorViewport(
    container,
    mount,
    options.scroller,
    options.header,
    editor,
  );
  const gaps = editor.createDecorationsCollection([]);
  let height = 0;
  let painted = false;
  const measure = (): void => {
    if (!painted) return;
    const next = editor.getContentHeight();
    if (height === next) return;
    height = next;
    container.style.height = `${next}px`;
    viewport.layout();
    options.onHeight(next);
  };
  const revealLine = (line: number): void => {
    options.onReveal();
    const lineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
    viewport.reveal(editor.getTopForLineNumber(line) - (viewport.bounds().height - lineHeight) / 2);
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
      const rect = container.getBoundingClientRect();
      const top = Math.max(bounds.top, rect.top) - rect.top;
      const bottom = Math.min(bounds.top + bounds.height, rect.bottom) - rect.top;
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
    painted: (markers) => {
      viewport.update(() => {
        const collapsed = collapseUnchanged(markers, model.getLineCount());
        gaps.set(collapsed.gapMarkers);
        editor.setHiddenAreas(collapsed.hidden, HIDDEN_AREAS_SOURCE);
        painted = true;
        measure();
      });
      options.onPainted();
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
        offset:
          viewport.bounds().top -
          container.getBoundingClientRect().top -
          editor.getTopForLineNumber(line),
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
  const inline = createInlineDiff(editor, presentation);
  const subscriptions = [
    editor.onDidContentSizeChange(measure),
    editor.onDidChangeCursorPosition((event) => options.onCursor(event.position.lineNumber)),
  ];
  options.configure(inline, model.uri.toString(), options.diff);
  return {
    capture,
    restore,
    focus: () => {
      editorContexts.activate(binding.connection);
      editor.focus();
    },
    layout: viewport.layout,
    inline,
    update: (diff) => options.configure(inline, model.uri.toString(), diff),
    dispose: () => {
      binding.dispose();
      for (const subscription of subscriptions) subscription.dispose();
      viewport.dispose();
      inline.dispose();
      gaps.clear();
      editor.dispose();
      mount.remove();
    },
  };
}
