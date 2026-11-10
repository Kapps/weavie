// One rendered preview's review of its file's pending changes: groups the diff into blocks, tracks which change
// is shown, and repaints the marks. The Monaco cursor under the preview is the review position, so the inline
// diff's Next, Keep and Revert act on exactly the change shown here.

import type * as monaco from "monaco-editor";
import { keyHint } from "../../../commands/key-hint";
import { CommandIds } from "../../../commands/types";
import type { DiffHunk, DiffMarkers } from "../../review/diff-markers";
import type { BlockList } from "../block-list";
import {
  type LineRange,
  type MarkdownBlock,
  markdownBlocks,
  markdownUnits,
  splitLines,
} from "../markdown-blocks";
import type { PreviewDocument } from "../preview-document";
import { previewEngine } from "../preview-markdown";
import { type BlockChange, blockChanges, mostlyRewritten } from "./block-changes";
import { ChangeMarks } from "./change-marks";

export interface PreviewReviewOptions {
  showSource(): void;
  toggleChangesOnly(): void;
  changesOnly(): boolean;
  /** Whether every change shows its word marks, not only the current one (`editor.markdownChangeMarks`). */
  marksAll(): boolean;
  /** Whether a block is open for editing, which freezes the page until it closes. */
  editing(): boolean;
}

export class PreviewReview {
  private readonly marks: ChangeMarks;
  private changes: BlockChange[] = [];
  private rewritten: string | undefined;
  // The baseline moves only on Keep or Revert, so its parse and render are kept until it does.
  private baseline: {
    text: string;
    units: LineRange[];
    lines: string[];
    blocks?: MarkdownBlock[];
  } = {
    text: "",
    units: [],
    lines: [""],
  };
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
    const current = side(model.getValue());
    if (this.baseline.text !== original) this.baseline = { text: original, ...side(original) };
    const found = blockChanges(markers.hunks, current, this.baseline);
    const marked = found.reduce((sum, c) => sum + c.modified.length + c.added.length, 0);
    this.rewritten = !mostlyRewritten(found, current.units.length, markers.isNewFile)
      ? undefined
      : markers.isNewFile
        ? "New file"
        : `Mostly rewritten · ${marked} of ${current.units.length} blocks changed`;
    this.changes = this.rewritten === undefined ? found : [];
    this.paint();
    return this.rewritten === undefined ? found.map((change) => change.hunk) : markers.hunks;
  }

  // Clearing between renders keeps the shown change, so a re-render lands on the same one.
  clear(): void {
    this.changes = [];
    this.rewritten = undefined;
    this.marks.clear();
  }

  /** Shows the change at `line`; false when the file is reviewed in Source instead. */
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

  /** Whether `hunk` is the change on screen; otherwise shows it, so nothing is kept or reverted unseen. */
  confirmTarget(hunk: DiffHunk): boolean {
    if (
      this.rewritten === undefined &&
      this.showing &&
      this.current()?.hunk === hunk &&
      this.onScreen(hunk)
    )
      return true;
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
    return pair && this.baseline.lines.slice(pair.baseline.start, pair.baseline.end).join("\n");
  }

  /** Repaints the marks for the page as rendered now; a block open for editing keeps the page as it is. */
  paint(): void {
    this.changesOnlyToggle?.setAttribute("aria-pressed", String(this.options.changesOnly()));
    if (this.options.editing()) return;
    const current = this.showing ? this.current() : undefined;
    this.marks.paint({
      changes: this.changes,
      rewritten: this.rewritten,
      shown: new Set(
        this.options.marksAll() ? this.changes : current === undefined ? [] : [current],
      ),
      changesOnly: this.options.changesOnly(),
      baselineBlocks: () => {
        this.baseline.blocks ??= markdownBlocks(previewEngine.markdown, this.baseline.text);
        return this.baseline.blocks;
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
    toggle.className = "weavie-inline-nav weavie-inline-changes-only ui-button";
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

  // Whether any of the hunk's blocks is inside the pane, so the change acted on is the one in view.
  private onScreen(hunk: DiffHunk): boolean {
    const pane = this.host.getBoundingClientRect();
    return this.list
      .units()
      .filter((unit) => {
        const [start, end] = this.list.lines(unit);
        return (
          start < hunk.currentEndExclusive - 1 && hunk.currentStart - 1 < Math.max(end, start + 1)
        );
      })
      .some((unit) => {
        const box = unit.getBoundingClientRect();
        return box.bottom > pane.top && box.top < pane.bottom;
      });
  }

  // The change at the review position, as the inline diff resolves it: the last one starting at or before it.
  private current(): BlockChange | undefined {
    const line = this.document.cursorLine();
    return this.changes.findLast((change) => change.hunk.anchorLine <= line) ?? this.changes[0];
  }
}

const side = (text: string) => ({
  units: markdownUnits(previewEngine.markdown, text),
  lines: splitLines(text),
});
