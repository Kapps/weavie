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
  // Each band's zone id → the stretch it reveals; Monaco's text layer owns clicks on zones.
  let gaps = new Map<string, { span: LineSpan; band: HTMLElement }>();
  let hovered: HTMLElement | undefined;
  const hover = (band: HTMLElement | undefined): void => {
    if (hovered === band) return;
    hovered?.classList.remove("hover");
    band?.classList.add("hover");
    hovered = band;
    mount.classList.toggle("gap-hover", band !== undefined);
    mount.title = band?.title ?? "";
  };
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
  const gapZone = (span: LineSpan): monaco.editor.IViewZone => {
    const count = span.end - span.start + 1;
    const band = document.createElement("div");
    band.className = "unified-review-gap";
    band.textContent = `Show ${count} unchanged line${count === 1 ? "" : "s"}`;
    band.title = `${band.textContent} — show the whole file${keyHint(CommandIds.reviewToggleContext)}`;
    // The band sits just above the hidden stretch it stands for.
    return {
      afterLineNumber: span.start - 1,
      heightInPx: GAP_HEIGHT,
      domNode: band,
      showInHiddenAreas: true,
    };
  };
  const applyContext = (): void => {
    if (markers === undefined) return;
    const hidden = collapseUnchanged(markers, model.getLineCount(), options.context());
    editor.setHiddenAreas(hidden, HIDDEN_AREAS_SOURCE);
    editor.changeViewZones((accessor) => {
      for (const id of gaps.keys()) accessor.removeZone(id);
      hover(undefined);
      gaps = new Map(
        hidden.map((range) => {
          const span = { start: range.startLineNumber, end: range.endLineNumber };
          const zone = gapZone(span);
          return [accessor.addZone(zone), { span, band: zone.domNode }];
        }),
      );
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
  container.classList.toggle("navigable", options.diff.currentExists);
  let pressed = "";
  let bandAnchor: number | undefined;
  const clickTarget = (event: monaco.editor.IEditorMouseEvent): string => {
    const { target } = event;
    if (target.type === monaco.editor.MouseTargetType.CONTENT_VIEW_ZONE)
      return gaps.has(target.detail.viewZoneId) ? target.detail.viewZoneId : "";
    return isLineNumber(target) ? `line:${target.position!.lineNumber}` : "";
  };
  const subscriptions = [
    contentSize,
    editor.onDidChangeCursorPosition((event) => options.onCursor(event.position.lineNumber)),
    editor.onMouseLeave(() => hover(undefined)),
    editor.onMouseMove((event) => {
      hover(gaps.get(clickTarget(event))?.band);
      if (options.diff.currentExists && isLineNumber(event.target))
        event.target.element!.title = `Open file at this line${keyHint(CommandIds.reviewOpenLine)}`;
    }),
    // A plain click on a band or line number acts; a drag or modified click keeps Monaco's selection.
    editor.onMouseDown((event) => {
      const { leftButton, shiftKey, ctrlKey, metaKey, altKey } = event.event;
      pressed =
        leftButton && !shiftKey && !ctrlKey && !metaKey && !altKey ? clickTarget(event) : "";
    }),
    editor.onMouseUp((event) => {
      const target = clickTarget(event);
      if (target === "" || target !== pressed) return;
      pressed = "";
      const gap = gaps.get(target);
      if (gap !== undefined) {
        // Hold the line beside the band still so the stretch opens in place.
        const { start, end } = gap.span;
        bandAnchor = start === 1 ? end + 1 : start - 1;
        options.revealContext(gap.span);
        bandAnchor = undefined;
      } else if (options.diff.currentExists)
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
    refreshContext: () => {
      const location = capture();
      if (bandAnchor !== undefined) {
        const offset = viewport.bounds().top - editor.getTopForLineNumber(bandAnchor);
        location.anchor = { line: bandAnchor, offset };
      }
      viewport.update(applyContext);
      restore(location);
    },
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
