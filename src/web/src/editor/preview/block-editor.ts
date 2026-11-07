// In-place editing for the Markdown preview. Opening a block swaps it for a textarea that writes straight through
// to the working copy, so undo, autosave and review all see ordinary edits and there is no draft to lose.
// See docs/specs/markdown-editable-preview.md.

import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";
import { notify } from "../../notify/notify";
import { gapInsertion, isBareMarker, nextListMarker, relocate, sourceOffset } from "./block-edit";
import { buildEditBox, HINT, SHOW_ORIGINAL, toggleOriginal } from "./block-editor-box";
import { registerBlockEditor } from "./block-editor-focus";
import { BlockList, type RenderedBlock } from "./block-list";
import { markdownBlocks, splitLines } from "./markdown-blocks";
import type { PreviewDocument } from "./preview-document";
import { previewEngine } from "./preview-markdown";

const IGNORED_CLICK = "a, button, input, summary, textarea, .wv-md-mark";

/** What the preview's change review tells a block that opens for editing. */
export interface BlockReview {
  /** Takes change marks off the page, so the block opens on its own text. */
  clearMarks(unit: Element): void;
  /** The agent's original source for the unit starting at `line`, when the agent changed it. */
  originalOf(line: number): string | undefined;
}

interface OpenBlock {
  unit: HTMLElement;
  block: RenderedBlock;
  textarea: HTMLTextAreaElement;
  box: HTMLElement;
  hidden: HTMLElement;
  start: number;
  lines: string[];
  originalLength: number;
  /** The agent's source for this block, when the agent changed it. */
  original: string | undefined;
  /** A list item this editor created is dropped again if nothing was typed into it. */
  created: boolean;
}

export class BlockEditor {
  /** The rendered blocks this editor edits, which the change review marks. */
  readonly list: BlockList;
  private content = "";
  private open: OpenBlock | undefined;
  private writing = false;
  private pointerDown = false;

  constructor(
    private readonly host: HTMLElement,
    body: HTMLElement,
    private readonly document: PreviewDocument,
    private readonly review: BlockReview,
    private readonly rendered: () => void,
  ) {
    this.list = new BlockList(body, editHint);
    registerBlockEditor(host, this);
    host.addEventListener("pointerdown", () => {
      this.pointerDown = true;
    });
    host.addEventListener("click", (event) => this.click(event));
    host.addEventListener("focusout", (event) => this.focusOut(event));
    host.addEventListener("keydown", (event) => this.undoKey(event));
    window.addEventListener("pointerup", this.releasePointer, true);
    window.addEventListener("pointercancel", this.releasePointer, true);
  }

  dispose(): void {
    this.closeOpen(false);
    window.removeEventListener("pointerup", this.releasePointer, true);
    window.removeEventListener("pointercancel", this.releasePointer, true);
  }

  /** Whether a block is open, so its textarea owns the keyboard. */
  get editing(): boolean {
    return this.open !== undefined;
  }

  /** Brings the blocks up to `content`, keeping every unchanged node. `rebuild` re-creates them all. */
  render(content: string, rebuild: boolean): void {
    this.content = content;
    if (this.writing) return;
    this.follow(splitLines(content));
    const open = this.open;
    const blocks = markdownBlocks(previewEngine.markdown, content).filter(
      (block) =>
        open === undefined ||
        block.start >= open.start + open.lines.length ||
        block.end <= open.start,
    );
    this.list.reconcile(blocks, open?.block, rebuild);
    this.rendered();
  }

  /** Scrolls the unit holding 0-based `line` to the top of the pane. */
  reveal(line: number): void {
    if (line === 0) this.host.scrollTop = 0;
    else this.list.unitNear(line)?.scrollIntoView({ block: "start" });
  }

  /** Opens the focused block (the Edit Block command); false when no block has focus. */
  editFocused(): boolean {
    const unit = document.activeElement;
    if (!(unit instanceof HTMLElement) || !this.list.isUnit(unit)) return false;
    return this.mount(unit, Number.POSITIVE_INFINITY, false);
  }

  /** Shows or hides the agent's original in the open block; false when there's none to show. */
  toggleOriginal(): boolean {
    const open = this.open;
    if (open?.original === undefined) return false;
    toggleOriginal(open.box, open.textarea.value, open.original);
    open.textarea.focus();
    return true;
  }

  /** Finishes the open block and returns the keyboard to it; false when none is open. */
  finish(): boolean {
    if (this.open === undefined) return false;
    this.closeOpen(true);
    return true;
  }

  private readonly releasePointer = (): void => {
    queueMicrotask(() => {
      this.pointerDown = false;
    });
  };

  // Another writer moved the open block, or replaced it: its text on disk is then what stands.
  private follow(lines: string[]): void {
    const open = this.open;
    if (open === undefined) return;
    const moved = relocate(lines, open.lines, open.start);
    if (moved === undefined) {
      this.open = undefined;
      this.list.remove(open.block);
      this.host.focus();
      notify("warn", "This block was changed outside the preview, so your editor closed.");
      return;
    }
    open.block.start += moved - open.start;
    open.block.end += moved - open.start;
    open.start = moved;
  }

  private click(event: MouseEvent): void {
    const target = event.target;
    if (!(target instanceof Element)) return;
    if (target.closest(`.${SHOW_ORIGINAL}`) !== null) {
      this.toggleOriginal();
      return;
    }
    if (target.closest(`.${HINT}`) !== null) {
      this.finish();
      return;
    }
    if (target.closest(IGNORED_CLICK) !== null) return;
    const unit = this.list.unitFrom(target);
    if (unit === undefined || !(window.getSelection()?.isCollapsed ?? true)) {
      this.closeOpen(false);
      if (unit === undefined && this.belowContent(event)) this.insertBlock(this.list.lastEnd());
      return;
    }
    const caret = renderedPrefix(unit, event);
    const open = this.open;
    const line = this.list.lines(unit)[0];
    const removed = this.closeOpen(false);
    // Stamps inside the open block's list predate its edits, so a sibling below shifts by what was typed.
    const shift =
      open !== undefined && line > open.start
        ? open.lines.length - open.originalLength - removed
        : 0;
    const reopened = unit.isConnected ? unit : this.list.unitAt(line + shift);
    if (reopened !== undefined) this.mount(reopened, caret, false);
  }

  private belowContent(event: MouseEvent): boolean {
    const body = this.host.firstElementChild as HTMLElement;
    return event.clientY > (body.lastElementChild?.getBoundingClientRect().bottom ?? 0);
  }

  // A pointer press closes the block on its click instead, so the layout can't shift under the press.
  private focusOut(event: FocusEvent): void {
    if (this.open === undefined || this.pointerDown || !document.hasFocus()) return;
    if (!(event.relatedTarget instanceof Node && this.open.box.contains(event.relatedTarget)))
      this.closeOpen(false);
  }

  private undoKey(event: KeyboardEvent): void {
    if (this.open !== undefined || !(event.ctrlKey || event.metaKey)) return;
    const key = event.key.toLowerCase();
    if (key !== "z" && key !== "y") return;
    event.preventDefault();
    if (key === "y" || event.shiftKey) this.document.redo();
    else this.document.undo();
  }

  private mount(unit: HTMLElement, caret: string | number, created: boolean): boolean {
    if (this.open !== undefined) return false;
    const text = this.document.editableText();
    if (text === undefined) {
      notify("info", "This file can't be edited here.");
      return false;
    }
    if (text !== this.content) return false;
    this.review.clearMarks(unit);
    const block = this.list.blockOf(unit) as RenderedBlock;
    const [start, end] = this.list.lines(unit);
    const lines = splitLines(text).slice(start, end);
    const original = this.review.originalOf(start);
    const code = block.node.hasAttribute("data-md-code");
    const { box, textarea } = buildEditBox(unit, lines.join("\n"), code, original);
    const hidden = document.createElement("div");
    hidden.hidden = true;
    hidden.append(
      ...[...unit.childNodes].filter(
        (node) => !(unit.matches("li") && node instanceof Element && node.matches("ul, ol")),
      ),
    );
    unit.classList.add("md-editing");
    unit.prepend(hidden, box);
    const originalLength = lines.length;
    this.open = {
      unit,
      block,
      textarea,
      box,
      hidden,
      start,
      lines,
      originalLength,
      original,
      created,
    };
    textarea.addEventListener("input", () => this.input());
    textarea.addEventListener("keydown", (event) => this.key(event));
    textarea.focus();
    const offset =
      typeof caret === "string"
        ? sourceOffset(textarea.value, caret)
        : Math.min(caret, textarea.value.length);
    textarea.setSelectionRange(offset, offset);
    this.document.checkpoint();
    return true;
  }

  private input(): void {
    const open = this.open as OpenBlock;
    const { value } = open.textarea;
    const lines = value.split("\n");
    const end = open.start + open.lines.length;
    open.block.end += lines.length - open.lines.length;
    open.lines = lines;
    open.box.dataset.value = value;
    open.created &&= isBareMarker(value);
    this.writing = true;
    try {
      this.document.replaceLines(open.start, end, lines);
    } finally {
      this.writing = false;
    }
  }

  private key(event: KeyboardEvent): void {
    const open = this.open as OpenBlock;
    const { value, selectionStart, selectionEnd } = open.textarea;
    if (event.isComposing || selectionStart !== selectionEnd) return;
    if (event.key === "Enter" && !event.shiftKey && selectionEnd === value.length) {
      if (open.block.node.hasAttribute("data-md-code") && open.unit === open.block.node) return;
      event.preventDefault();
      if (value !== "") this.continueAfter();
    } else if (event.key === "ArrowUp" && selectionStart === 0) {
      event.preventDefault();
      this.step(-1);
    } else if (event.key === "ArrowDown" && selectionEnd === value.length) {
      event.preventDefault();
      this.step(1);
    } else if (event.key === "Backspace" && value === "") {
      event.preventDefault();
      this.step(-1);
    }
  }

  // Enter at the end of a block: the next list item, or a new block below. A bare new item ends its list.
  private continueAfter(): void {
    const open = this.open as OpenBlock;
    const after = open.start + open.lines.length;
    const item = open.unit.matches("li");
    const endsList = item && open.created;
    this.closeOpen(false);
    if (!item || endsList) {
      this.insertBlock(endsList ? open.start : after);
      return;
    }
    this.document.replaceLines(after, after, [nextListMarker(open.lines[0] ?? "")]);
    const next = this.list.unitAt(after);
    if (next !== undefined) this.mount(next, Number.POSITIVE_INFINITY, true);
  }

  private insertBlock(after: number): void {
    const text = this.document.editableText();
    if (text === undefined) {
      notify("info", "This file can't be edited here.");
      return;
    }
    const { at, insert, line } = gapInsertion(splitLines(text), after);
    if (insert.length > 0) this.document.replaceLines(at, at, insert);
    this.mount(this.list.addEmpty(line).node, 0, false);
  }

  private step(direction: -1 | 1): void {
    const open = this.open as OpenBlock;
    const last = open.start + open.lines.length - 1;
    const removed = this.closeOpen(false);
    const ranges = this.list.units().map((unit) => ({ unit, range: this.list.lines(unit) }));
    const target =
      direction < 0
        ? ranges.findLast(({ range }) => range[1] <= open.start)
        : ranges.find(({ range }) => range[0] > last - removed);
    if (target !== undefined)
      this.mount(target.unit, direction < 0 ? Number.POSITIVE_INFINITY : 0, false);
    else this.host.focus();
  }

  // Returns how many lines closing removed, so callers holding line numbers can adjust.
  private closeOpen(focus: boolean): number {
    const open = this.open;
    if (open === undefined) return 0;
    this.open = undefined;
    open.unit.classList.remove("md-editing");
    open.box.remove();
    open.hidden.replaceWith(...open.hidden.childNodes);
    const text = open.lines.join("\n");
    const empty = text.trim() === "" || (open.created && isBareMarker(text));
    const removed = empty ? this.removeLines(open) : 0;
    this.document.checkpoint();
    this.render(this.document.editableText() ?? this.content, false);
    if (focus) (this.list.unitAt(open.start) ?? this.host).focus();
    return removed;
  }

  // The blank below goes too only when one remains above; otherwise it's what separates the neighbors.
  private removeLines(open: OpenBlock): number {
    if (this.document.editableText() === undefined) return 0;
    const lines = splitLines(this.content);
    const end = open.start + open.lines.length;
    const blankAbove = open.start === 0 || lines[open.start - 1]?.trim() === "";
    const through = blankAbove && lines[end]?.trim() === "" ? end + 1 : end;
    this.document.replaceLines(open.start, through, []);
    return through - open.start;
  }
}

// The rendered text before the click, which sourceOffset maps back onto the block's markdown.
function renderedPrefix(unit: HTMLElement, event: MouseEvent): string {
  const point = caretAt(event.clientX, event.clientY);
  if (point === undefined || !unit.contains(point.node)) return "";
  const range = document.createRange();
  range.selectNodeContents(unit);
  range.setEnd(point.node, point.offset);
  const before = range.cloneContents();
  for (const mark of before.querySelectorAll(".wv-md-del, .wv-md-mark")) mark.remove();
  return before.textContent ?? "";
}

// Safari only has the older caretRangeFromPoint; everything else has the standard call.
function caretAt(x: number, y: number): { node: Node; offset: number } | undefined {
  if ("caretPositionFromPoint" in document) {
    const caret = document.caretPositionFromPoint(x, y);
    return caret === null ? undefined : { node: caret.offsetNode, offset: caret.offset };
  }
  const range = (document as Document).caretRangeFromPoint(x, y);
  return range === null ? undefined : { node: range.startContainer, offset: range.startOffset };
}

const editHint = (): string => `Edit block${keyHint(CommandIds.previewEditBlock)}`;
