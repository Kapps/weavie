import { CodeLensContribution } from "@codingame/monaco-vscode-api/vscode/vs/editor/contrib/codelens/browser/codelensController";
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
import { createReviewEditorViewport, type ReviewSectionGeometry } from "./review-editor-viewport";
import type { ReviewScroll } from "./review-scroll";
import type { LineSpan, ReviewFileDiff } from "./review-store";

const HIDDEN_AREAS_SOURCE = "weavie.review";
const GAP_HEIGHT = 24;
// Lucide's chevrons-up-down.
const EXPAND_ICON =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m7 15 5 5 5-5"/><path d="m7 9 5-5 5 5"/></svg>';
type CollapsingEditor = monaco.editor.IStandaloneCodeEditor & {
  setHiddenAreas(ranges: monaco.IRange[], source: unknown): void;
};

export interface ReviewEditor {
  capture(): TextLocation;
  restore(location: TextLocation): void;
  revealFileStart(line: number): void;
  focus(): void;
  measured(): void;
  /** Re-applies the collapsed stretches after the file's revealed context changed. */
  refreshContext(): void;
  position(): void;
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
  section: ReviewSectionGeometry;
  model: monaco.editor.ITextModel;
  editable: boolean;
  path: string;
  currentExists: boolean;
  active: () => boolean;
  onToolbar: () => void;
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
    (dimension) => {
      const editor = createEmbeddedEditor(
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
      );
      // Cached CodeLens zones must precede the first published section height.
      if (editor.getContribution(CodeLensContribution.ID) === null) {
        throw new Error("Monaco CodeLens contribution is not registered.");
      }
      return editor;
    },
    options.section,
  );
  const editor = viewport.editor as CollapsingEditor;
  // Undefined until InlineDiff first lays out the diff; null for a timed-out diff.
  let markers: DiffMarkers | null | undefined;
  let hidden: monaco.IRange[] = [];
  // Each band's zone id → the stretch it reveals; Monaco's text layer owns clicks on zones.
  interface Gap {
    span: LineSpan;
    zone: monaco.editor.IViewZone & { marginDomNode: HTMLElement };
  }
  let gaps = new Map<string, Gap>();
  let hovered: Gap | undefined;
  const hover = (gap: Gap | undefined): void => {
    if (hovered === gap) return;
    for (const node of [hovered?.zone.domNode, hovered?.zone.marginDomNode])
      node?.classList.remove("hover");
    for (const node of [gap?.zone.domNode, gap?.zone.marginDomNode]) node?.classList.add("hover");
    hovered = gap;
    mount.classList.toggle("gap-hover", gap !== undefined);
    mount.title = gap?.zone.domNode.title ?? "";
  };
  let disposed = false;
  const publish = (): void => {
    if (!disposed) options.onPainted();
  };
  let geometryReady = false;
  let pendingContextLocation: TextLocation | undefined;
  let height = 0;
  const measure = (): void => {
    if (!geometryReady) return;
    const next = editor.getContentHeight();
    if (height === next) return;
    height = next;
    container.style.height = `${next}px`;
    viewport.setContentHeight(next);
    options.onHeight(height);
  };
  const revealLine = (line: number): void => {
    const lineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
    viewport.reveal(editor.getTopForLineNumber(line) - (viewport.bounds().height - lineHeight) / 2);
  };
  const gapZone = (span: LineSpan): Gap["zone"] => {
    const count = span.end - span.start + 1;
    // Monaco forces zone nodes to display:block, so each half lays out through an inner row.
    const band = document.createElement("div");
    band.className = "unified-review-gap";
    const label = document.createElement("span");
    label.textContent = `Show ${count} unchanged line${count === 1 ? "" : "s"}`;
    band.append(label);
    band.title = `${label.textContent} — show the whole file${keyHint(CommandIds.reviewToggleContext)}`;
    // The gutter half carries the band across the line numbers, with its expand glyph under them.
    const margin = document.createElement("div");
    margin.className = "unified-review-gap-margin";
    margin.innerHTML = `<span>${EXPAND_ICON}</span>`;
    // The band sits just above the hidden stretch it stands for.
    return {
      afterLineNumber: span.start - 1,
      heightInPx: GAP_HEIGHT,
      domNode: band,
      marginDomNode: margin,
      showInHiddenAreas: true,
    };
  };
  const applyContext = (): void => {
    if (markers === undefined) return;
    hidden = collapseUnchanged(markers, model.getLineCount(), options.context());
    editor.setHiddenAreas(hidden, HIDDEN_AREAS_SOURCE);
    editor.changeViewZones((accessor) => {
      for (const id of gaps.keys()) accessor.removeZone(id);
      hover(undefined);
      gaps = new Map(
        hidden.map((range) => {
          const span = { start: range.startLineNumber, end: range.endLineNumber };
          const zone = gapZone(span);
          return [accessor.addZone(zone), { span, zone }];
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
    publishToolbar: options.onToolbar,
    revealLine,
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
        mount.style.removeProperty("visibility");
      }
      publish();
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
    reveal: (range) => {
      viewport.update(() =>
        editor.revealRangeInCenterIfOutsideViewport(range, monaco.editor.ScrollType.Immediate),
      );
      const top = editor.getTopForPosition(range.startLineNumber, range.startColumn);
      const bottom =
        editor.getTopForPosition(range.endLineNumber, range.endColumn) +
        editor.getOption(monaco.editor.EditorOption.lineHeight);
      const bounds = viewport.bounds();
      if (top < bounds.top || bottom > bounds.bottom) {
        viewport.reveal((top + bottom - bounds.height) / 2);
      }
    },
  });
  widgets.addEventListener("focusin", () => {
    editorContexts.activate(binding.connection);
    options.onCursor(editor.getPosition()?.lineNumber ?? 1);
  });
  const inline = createInlineDiff(editor, presentation);
  const contentSize = editor.onDidContentSizeChange(measure);
  container.classList.toggle("navigable", options.currentExists);
  let pressed = "";
  let bandAnchor: number | undefined;
  const clickTarget = (event: monaco.editor.IEditorMouseEvent): string => {
    const { target } = event;
    if (
      target.type === monaco.editor.MouseTargetType.CONTENT_VIEW_ZONE ||
      target.type === monaco.editor.MouseTargetType.GUTTER_VIEW_ZONE
    )
      return gaps.has(target.detail.viewZoneId) ? target.detail.viewZoneId : "";
    return isLineNumber(target) ? `line:${target.position!.lineNumber}` : "";
  };
  const subscriptions = [
    contentSize,
    editor.onDidChangeCursorPosition((event) => {
      if (editor.hasWidgetFocus()) options.onCursor(event.position.lineNumber);
    }),
    editor.onMouseLeave(() => hover(undefined)),
    editor.onMouseMove((event) => {
      hover(gaps.get(clickTarget(event)));
      if (options.currentExists && isLineNumber(event.target))
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
      } else if (options.currentExists)
        void runCommandWithFeedback(CommandIds.reviewOpen, {
          path: options.path,
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
    measured: () => {
      const location = pendingContextLocation;
      pendingContextLocation = undefined;
      if (!disposed && location !== undefined) restore(location);
    },
    refreshContext: () => {
      if (!geometryReady) return;
      const location = pendingContextLocation ?? capture();
      if (bandAnchor !== undefined) {
        const offset = viewport.bounds().top - editor.getTopForLineNumber(bandAnchor);
        location.anchor = { line: bandAnchor, offset };
      }
      viewport.update(applyContext);
      pendingContextLocation = location;
      options.onPainted();
    },
    position: viewport.position,
    inline,
    update: (diff) => options.configure(inline, model.uri.toString(), diff),
    dispose: () => {
      disposed = true;
      pendingContextLocation = undefined;
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
