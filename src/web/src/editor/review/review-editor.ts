import {
  createInlineDiff,
  type InlineDiff,
  type InlineDiffPresentation,
  type ReviewScopeState,
} from "../inline-diff";
import { createEmbeddedEditor, type monaco } from "../monaco-setup";
import { collapseUnchanged } from "./review-context";
import { createReviewEditorViewport } from "./review-editor-viewport";
import type { ReviewFileDiff } from "./review-store";

const HIDDEN_AREAS_SOURCE = "weavie.review";
type CollapsingEditor = monaco.editor.IStandaloneCodeEditor & {
  setHiddenAreas(ranges: monaco.IRange[], source: unknown): void;
};

export interface ReviewEditor {
  layout(): void;
  inline: InlineDiff;
  reveal(line: number): void;
  line(): number;
  update(diff: ReviewFileDiff): void;
  dispose(): void;
}

/** The section owns sizing and collapsed context; InlineDiff owns all review rendering and actions. */
export function createReviewEditor(options: {
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
  // Keeps the section's real DOM height — and so the viewport's scroll-clamp bound in
  // review-editor-viewport.ts — matched to Monaco's actual content height as soon as it's known, rather
  // than waiting on the diff to paint. Waiting left the section sized to its rough pre-paint estimate
  // (estimatedEditorHeight), which could still be shorter than Monaco's real content height when a
  // keyboard/caret reveal fired, permanently capping how far it could scroll (e.g. Ctrl+End could never
  // reach a large diff's last lines).
  const measure = (): void => {
    const next = editor.getContentHeight();
    if (height === next) return;
    height = next;
    container.style.height = `${next}px`;
    viewport.layout();
    options.onHeight(next);
  };
  const presentation: InlineDiffPresentation = {
    scope: options.scope,
    updateGeometry: viewport.update,
    parked: () => false,
    active: options.active,
    toolbarHost: options.toolbarHost,
    revealLine: (line) => {
      options.onReveal();
      viewport.reveal(editor.getTopForLineNumber(line));
    },
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
        measure();
      });
      options.onPainted();
    },
  };
  const inline = createInlineDiff(editor, presentation);
  const subscriptions = [
    editor.onDidContentSizeChange(measure),
    editor.onDidChangeCursorPosition((event) => options.onCursor(event.position.lineNumber)),
  ];
  // Monaco already knows the model's content height as soon as it's attached, ahead of any content-size
  // event this subscription (registered after that attach) could observe.
  measure();
  options.configure(inline, model.uri.toString(), options.diff);
  return {
    layout: viewport.layout,
    inline,
    line: () => editor.getPosition()?.lineNumber ?? 1,
    reveal: (line) => {
      editor.setPosition({ lineNumber: line, column: 1 });
      presentation.revealLine(line);
    },
    update: (diff) => options.configure(inline, model.uri.toString(), diff),
    dispose: () => {
      for (const subscription of subscriptions) subscription.dispose();
      viewport.dispose();
      inline.dispose();
      gaps.clear();
      editor.dispose();
      mount.remove();
    },
  };
}
