// One rendered preview's review of its file's pending changes: groups the diff into blocks, tracks which change
// is shown, and repaints the marks. The Monaco cursor under the preview is the review position, so the inline
// diff's Next, Keep and Revert act on exactly the change shown here.

import type * as monaco from "monaco-editor";
import { keyHint } from "../../../commands/key-hint";
import { CommandIds } from "../../../commands/types";
import type { DiffHunk, DiffMarkers } from "../../review/diff-markers";
import type { BlockList } from "../block-list";
import { type LineRange, markdownBlocks, markdownUnits, splitLines } from "../markdown-blocks";
import type { PreviewDocument } from "../preview-document";
import { previewEngine } from "../preview-markdown";
import { type BlockChange, blockChanges, mostlyRewritten } from "./block-changes";
import { ChangeMarks } from "./change-marks";
import type { PreviewReviewHandle } from "./registry";

export interface PreviewReviewOptions {
  showSource(): void;
  toggleChangesOnly(): void;
  changesOnly(): boolean;
  /** Whether every change shows its word marks, not only the current one (`editor.markdownChangeMarks`). */
  marksAll(): boolean;
  /** Whether a block is open for editing, which freezes the page until it closes. */
  editing(): boolean;
}

export class PreviewReview implements PreviewReviewHandle {
  private readonly marks: ChangeMarks;
  private changes: BlockChange[] = [];
  private rewritten: { label: string } | undefined;
  private baseline = "";
  private oldSide: { text: string; units: LineRange[]; lines: string[] } | undefined;
  private baselineBlocks: { text: string; blocks: ReturnType<typeof markdownBlocks> } | undefined;
  private showing = false;
  private changesOnlyToggle: HTMLElement | undefined;

  constructor(
    private readonly list: BlockList,
    private readonly host: HTMLElement,
    body: HTMLElement,
    private readonly document: PreviewDocument,
    private readonly options: PreviewReviewOptions,
  ) {
    this.marks = new ChangeMarks(list, host, body);
  }

  group(markers: DiffMarkers, original: string, model: monaco.editor.ITextModel): DiffHunk[] {
    const side = (text: string) => ({
      units: markdownUnits(previewEngine.markdown, text),
      lines: splitLines(text),
    });
    const current = side(model.getValue());
    // The baseline only moves on Keep or Revert, so its parse is reused across renders.
    if (this.oldSide?.text !== original) this.oldSide = { text: original, ...side(original) };
    const found = blockChanges(markers.hunks, current, this.oldSide);
    const rewritten = mostlyRewritten(found, current.units.length, markers.isNewFile);
    const marked = found.reduce((sum, c) => sum + c.modified.length + c.added.length, 0);
    this.rewritten = !rewritten
      ? undefined
      : {
          label: markers.isNewFile
            ? "New file"
            : `Mostly rewritten · ${marked} of ${current.units.length} blocks changed`,
        };
    this.changes = rewritten ? [] : found;
    this.baseline = original;
    this.paint();
    return rewritten ? markers.hunks : found.map((change) => change.hunk);
  }

  // Clearing between renders keeps the shown change, so a re-render lands on the same one.
  clear(): void {
    this.changes = [];
    this.rewritten = undefined;
    this.marks.clear();
  }

  revealLine(line: number): boolean {
    if (this.rewritten !== undefined) {
      this.options.showSource();
      return false;
    }
    this.showing = true;
    this.paint();
    this.list.unitNear(line - 1)?.scrollIntoView({ block: "nearest" });
    return true;
  }

  focus(): void {
    this.host.focus();
  }

  confirmTarget(hunk: DiffHunk): boolean {
    if (this.rewritten !== undefined) {
      this.options.showSource();
      return false;
    }
    if (this.showing && this.current()?.hunk === hunk) return true;
    this.document.setCursor(hunk.anchorLine);
    this.revealLine(hunk.anchorLine);
    return false;
  }

  /** Returns the shown change to a plain bar (the Hide Change Marks command); false when none is shown. */
  dismiss(): boolean {
    if (!this.showing) return false;
    this.showing = false;
    this.paint();
    return true;
  }

  /** The agent's source for the unit starting at `line`, when the unit is a change from it. */
  originalOf(line: number): string | undefined {
    const pair = this.changes.flatMap((c) => c.modified).find((m) => m.current.start === line);
    return pair === undefined ? undefined : text(splitLines(this.baseline), pair.baseline);
  }

  /** Repaints the marks for the page as rendered now; a block open for editing keeps the page as it is. */
  paint(): void {
    this.changesOnlyToggle?.setAttribute("aria-pressed", String(this.options.changesOnly()));
    if (this.options.editing()) return;
    const current = this.showing ? this.current() : undefined;
    const shown = new Set(
      this.options.marksAll() ? this.changes : current === undefined ? [] : [current],
    );
    this.marks.paint({
      changes: this.changes,
      rewritten: this.rewritten,
      shown,
      changesOnly: this.options.changesOnly(),
      baselineBlocks: () => this.oldBlocks(),
      titles: {
        show: `Show changes${keyHint(CommandIds.nextChange)}`,
        source: `Show diff in Source${keyHint(CommandIds.toggleEditorPreview)}`,
      },
      onShow: (change) => {
        this.document.setCursor(change.hunk.anchorLine);
        this.revealLine(change.hunk.anchorLine);
        this.focus();
      },
      onSource: () => this.options.showSource(),
    });
  }

  /** The review toolbar's Changes only toggle, while there are block changes to fold around. */
  toolbarItems(): HTMLElement[] {
    if (this.changes.length === 0) return [];
    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "weavie-inline-nav weavie-inline-changes-only";
    toggle.textContent = "Changes only";
    toggle.title = `Show only the changed blocks${keyHint(CommandIds.previewChangesOnly)}`;
    toggle.setAttribute("aria-pressed", String(this.options.changesOnly()));
    toggle.addEventListener("click", () => this.options.toggleChangesOnly());
    this.changesOnlyToggle = toggle;
    return [toggle];
  }

  /** Takes the marks off `unit` so it can open on its own text; the rest of the page stays put. */
  clearMarks(unit: Element): void {
    this.marks.clearUnit(unit);
  }

  // The change at the review position, as the inline diff resolves it: the last one starting at or before it.
  private current(): BlockChange | undefined {
    const line = this.document.cursorLine();
    return this.changes.findLast((change) => change.hunk.anchorLine <= line) ?? this.changes[0];
  }

  private oldBlocks(): ReturnType<typeof markdownBlocks> {
    if (this.baselineBlocks?.text !== this.baseline)
      this.baselineBlocks = {
        text: this.baseline,
        blocks: markdownBlocks(previewEngine.markdown, this.baseline),
      };
    return this.baselineBlocks.blocks;
  }
}

const text = (lines: string[], range: LineRange): string =>
  lines.slice(range.start, range.end).join("\n");
