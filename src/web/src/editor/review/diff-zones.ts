// The view zones an inline diff hangs off a live model — removed-line ghosts and the new-file band — and the
// cadence both surfaces repaint at. Shared by the file-review editor and the unified review's per-file editors
// so a diff reads identically wherever it is shown.

import { monaco } from "../monaco-setup";
import type { DiffMarkers } from "./diff-markers";

// Height of the "New file" header band shown above a wholly-new file's first line.
const NEW_FILE_BADGE_HEIGHT = 24;

/**
 * Repaint debounce, so typing into a file under review doesn't recompute the diff and re-lay-out its zones on
 * every keystroke.
 */
export const DIFF_RECOMPUTE_DEBOUNCE_MS = 120;

/** Adds the new-file band and every removed-lines ghost for `markers`, returning the zone ids to hold. */
export function addDiffZones(
  editor: monaco.editor.ICodeEditor,
  accessor: monaco.editor.IViewZoneChangeAccessor,
  markers: DiffMarkers,
): string[] {
  const ids: string[] = [];
  if (markers.isNewFile) {
    ids.push(
      accessor.addZone({
        afterLineNumber: 0,
        heightInPx: NEW_FILE_BADGE_HEIGHT,
        domNode: buildNewFileBadge(),
      }),
    );
  }
  for (const ghost of markers.ghosts) {
    const view = buildGhostLines(editor, ghost.lines, ghost.faded);
    ids.push(
      accessor.addZone({
        afterLineNumber: ghost.afterLineNumber,
        heightInLines: ghost.lines.length,
        domNode: view.node,
        onDomNodeTop: view.onDomNodeTop,
      }),
    );
  }
  return ids;
}

// The removed lines of one hunk, laid out on `editor`'s resolved metrics so the zone height matches exactly.
function buildGhostLines(
  editor: monaco.editor.ICodeEditor,
  lines: string[],
  faded: boolean,
): { node: HTMLElement; onDomNodeTop: (top: number) => void } {
  const node = document.createElement("div");
  // Faded variant: a removed line in an already-accepted hunk, dimmed to match its faded green counterpart.
  node.className = faded
    ? "weavie-inline-removed weavie-inline-removed-line weavie-inline-removed-faded"
    : "weavie-inline-removed weavie-inline-removed-line";
  // Use the resolved metrics, not the raw font setting: the view zone reserves `lines.length * lineHeight`
  // px, so the ghost rows must use that same line height or they overflow the zone.
  const fontInfo = editor.getOption(monaco.editor.EditorOption.fontInfo);
  node.style.fontFamily = fontInfo.fontFamily;
  node.style.fontSize = `${fontInfo.fontSize}px`;
  node.style.lineHeight = `${fontInfo.lineHeight}px`;
  // Render tabs at the editor's tab width so a removed line's leading indentation lines up with the live
  // code, instead of CSS `tab-size`'s default of 8.
  node.style.tabSize = String(editor.getModel()?.getOptions().tabSize ?? 4);
  node.dataset.lineCount = String(lines.length);
  const content = document.createElement("div");
  content.className = "weavie-inline-removed-content";
  node.appendChild(content);
  let renderedStart = -1;
  let renderedEnd = -1;
  const onDomNodeTop = (top: number): void => {
    const overscan = 20;
    const viewportHeight = editor.getLayoutInfo().height;
    const start = Math.max(0, Math.floor(-top / fontInfo.lineHeight) - overscan);
    const end = Math.min(
      lines.length,
      Math.max(0, Math.ceil((viewportHeight - top) / fontInfo.lineHeight) + overscan),
    );
    if (start === renderedStart && end === renderedEnd) {
      return;
    }
    renderedStart = start;
    renderedEnd = end;
    content.style.transform = `translateY(${start * fontInfo.lineHeight}px)`;
    content.textContent = lines
      .slice(start, end)
      .map((line) => (line.length === 0 ? " " : line))
      .join("\n");
  };
  onDomNodeTop(0);
  return { node, onDomNodeTop };
}

// The "New file" header band: a sans-serif green pill above a wholly-new file's first line, so an all-added
// file is labelled once instead of washed green on every line.
function buildNewFileBadge(): HTMLElement {
  const node = document.createElement("div");
  node.className = "weavie-inline-newfile";
  const tag = document.createElement("span");
  tag.className = "weavie-inline-newfile-tag";
  tag.textContent = "New file";
  node.appendChild(tag);
  return node;
}
