// Paints a preview's changes: margin bars, removal chips, word marks on the shown change, folds, and the rewritten
// banner. Everything it adds is tracked and removed on the next paint, so the keyed block nodes stay clean.

import { keyHint } from "../../../commands/key-hint";
import { CommandIds } from "../../../commands/types";
import type { BlockList } from "../block-list";
import { ITEM_END, ITEM_START, type LineRange, type MarkdownBlock } from "../markdown-blocks";
import { previewEngine } from "../preview-markdown";
import type { BlockChange } from "./block-changes";
import { type WordDiff, wordDiff } from "./word-diff";

/** Marks are never edit targets or unit text; the block editor ignores clicks inside them. */
export const MARK = "wv-md-mark";

// How far left of the text column the margin bars sit, in pixels.
const BAR_GAP = 14;

export interface MarksState {
  changes: BlockChange[];
  /** "Mostly rewritten" or "New file", when per-block marks would mark nearly everything. */
  rewritten: string | undefined;
  /** Changes showing word marks: the current one, or all of them. */
  shown: ReadonlySet<BlockChange>;
  changesOnly: boolean;
  baselineBlocks(): MarkdownBlock[];
  onShow(change: BlockChange): void;
  onSource(): void;
}

/** Removed text, put back where it was. */
export const removedText = (text: string): HTMLElement => make("del", `${MARK} wv-md-del`, text);

/** The note shown where word marks would take the diff engine too long. */
export function tooManyChangesNote(): HTMLElement {
  return make("span", `${MARK} wv-md-note`, "Too many changes to mark words");
}

export class ChangeMarks {
  private readonly added: Element[] = [];
  private readonly classed: Element[] = [];
  private readonly words: Element[] = [];
  private readonly expanded = new WeakSet<Element>();

  constructor(
    private readonly list: BlockList,
    private readonly host: HTMLElement,
    private readonly body: HTMLElement,
  ) {}

  /** Takes the marks off one unit, leaving the rest of the page as it is. */
  clearUnit(unit: Element): void {
    unmark(this.words.filter((mark) => unit.contains(mark)));
    for (const element of this.added) if (unit.contains(element)) element.remove();
  }

  clear(): void {
    unmark(this.words);
    for (const element of this.added) element.remove();
    for (const element of this.classed)
      element.classList.remove("wv-md-changed", "wv-md-added", "wv-md-folded");
    this.words.length = 0;
    this.added.length = 0;
    this.classed.length = 0;
  }

  paint(state: MarksState): void {
    this.clear();
    if (state.rewritten !== undefined) {
      const banner = this.track(make("div", `${MARK} wv-md-banner`, ""));
      const label = "Show the diff in Source";
      const title = `${label}${keyHint(CommandIds.toggleEditorPreview)}`;
      banner.append(
        make("strong", "", state.rewritten),
        action("wv-md-action", label, title, state.onSource),
      );
      this.host.insertBefore(banner, this.body);
      return;
    }
    const marked = new Set<Element>();
    for (const change of state.changes) {
      const shown = state.shown.has(change);
      for (const { current, baseline } of change.modified) {
        const unit = this.markUnit(current, "wv-md-changed", change, state, marked);
        if (shown && unit !== undefined) this.markWords(unit, this.oldUnit(baseline, state));
      }
      for (const range of change.added) this.markUnit(range, "wv-md-added", change, state, marked);
      if (change.removed.length > 0) this.removal(change, shown, state, marked);
    }
    if (state.changesOnly) this.fold(marked);
    // Every bar sits in one gutter left of the text column, however deep its list item is nested.
    const gutter = this.body.getBoundingClientRect().left - BAR_GAP;
    for (const bar of this.added.filter((element) => element.classList.contains("wv-md-bar")))
      (bar as HTMLElement).style.left =
        `${gutter - (bar.parentElement as HTMLElement).getBoundingClientRect().left}px`;
  }

  private markUnit(
    range: LineRange,
    kind: string,
    change: BlockChange,
    state: MarksState,
    marked: Set<Element>,
  ): HTMLElement | undefined {
    const unit = this.list.unitAt(range.start);
    if (unit === undefined) return undefined;
    unit.classList.add(kind);
    this.classed.push(unit);
    unit.prepend(this.bar(change, state));
    marked.add(this.topLevel(unit));
    return unit;
  }

  // A removal sits where its blocks were: a chip, or their old text while its change is shown.
  private removal(
    change: BlockChange,
    shown: boolean,
    state: MarksState,
    marked: Set<Element>,
  ): void {
    const next = this.list.units().find((unit) => this.list.lines(unit)[0] >= change.at);
    const removal = this.track(
      make(next?.localName === "li" ? "li" : "div", `${MARK} wv-md-removed`, ""),
    );
    const old = change.removed.map((range) => this.oldUnit(range, state));
    const label = make("span", "wv-md-removed-label", "Removed");
    if (shown) {
      const body = make("div", "wv-md-removed-body", "");
      body.append(label, ...old);
      removal.append(this.bar(change, state), body);
    } else {
      const chip = action("wv-md-removed-chip", "", showTitle(), () => state.onShow(change));
      const summary = old
        .map((unit) => unit.textContent ?? "")
        .join(" ")
        .trim();
      chip.append(label, make("span", "wv-md-removed-summary", summary));
      removal.append(this.bar(change, state), chip);
    }
    if (next === undefined) this.body.append(removal);
    else next.before(removal);
    marked.add(this.topLevel(next ?? removal));
  }

  // Folds each run of two or more unmarked top-level blocks behind a divider that shows it again.
  private fold(marked: Set<Element>): void {
    let run: Element[] = [];
    const flush = (): void => {
      const blocks = run;
      run = [];
      const first = blocks[0];
      if (first === undefined || blocks.length < 2 || this.expanded.has(first)) return;
      for (const node of blocks) node.classList.add("wv-md-folded");
      this.classed.push(...blocks);
      const row = this.track(make("div", `${MARK} wv-md-fold`, ""));
      const show = action("wv-md-action", "Show", "Show these blocks", () => {
        this.expanded.add(first);
        row.remove();
        for (const node of blocks) node.classList.remove("wv-md-folded");
      });
      row.append(make("span", "", `${blocks.length} unchanged blocks`), show);
      first.before(row);
    };
    for (const node of this.body.children) {
      if (node.classList.contains(MARK)) continue;
      if (marked.has(node)) flush();
      else run.push(node);
    }
    flush();
  }

  private markWords(unit: HTMLElement, old: HTMLElement): void {
    if (this.list.blockOf(unit)?.node.hasAttribute("data-md-code")) return;
    const diff = wordDiff(ownText(old), ownText(unit));
    if (diff === null) unit.append(this.track(tooManyChangesNote()));
    else this.words.push(...markText(unit, diff));
  }

  // The baseline's rendered unit, sanitized like every preview block and stripped of line stamps, so the block
  // list never takes it for a unit.
  private oldUnit(range: LineRange, state: MarksState): HTMLElement {
    const block = state.baselineBlocks().findLast((candidate) => candidate.start <= range.start);
    const holder = document.createElement("div");
    if (block === undefined) return holder;
    holder.append(previewEngine.sanitize(block.html));
    const item = holder.querySelector<HTMLElement>(
      `li[${ITEM_START}="${range.start - block.start}"]`,
    );
    for (const stamped of holder.querySelectorAll(`[${ITEM_START}]`)) {
      stamped.removeAttribute(ITEM_START);
      stamped.removeAttribute(ITEM_END);
    }
    if (item === null) return holder;
    for (const nested of item.querySelectorAll(":scope > ul, :scope > ol")) nested.remove();
    return item;
  }

  private bar(change: BlockChange, state: MarksState): HTMLButtonElement {
    const bar = action("wv-md-bar", "", showTitle(), () => state.onShow(change));
    bar.setAttribute("aria-label", bar.title);
    return this.track(bar);
  }

  private topLevel(element: Element): Element {
    let node = element;
    while (node.parentElement !== null && node.parentElement !== this.body)
      node = node.parentElement;
    return node;
  }

  private track<T extends Element>(element: T): T {
    this.added.push(element);
    return element;
  }
}

const showTitle = (): string => `Show changes${keyHint(CommandIds.nextChange)}`;

function make(tag: string, className: string, text: string): HTMLElement {
  const element = document.createElement(tag);
  element.className = className;
  element.textContent = text;
  return element;
}

function action(
  className: string,
  label: string,
  title: string,
  run: () => void,
): HTMLButtonElement {
  const button = make("button", `${MARK} ${className}`, label) as HTMLButtonElement;
  button.type = "button";
  button.title = title;
  button.addEventListener("click", (event) => {
    event.stopPropagation();
    run();
  });
  return button;
}

function unmark(marks: Element[]): void {
  for (const mark of marks) {
    const parent = mark.parentNode;
    if (mark.localName === "ins") mark.replaceWith(...mark.childNodes);
    else mark.remove();
    parent?.normalize();
  }
}

// The text a unit renders itself: a list item's nested lists and every mark belong to other units.
function ownTextNodes(unit: Element): Text[] {
  const walker = document.createTreeWalker(unit, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
    acceptNode: (node) => {
      if (node instanceof Text)
        return node.data === "" ? NodeFilter.FILTER_SKIP : NodeFilter.FILTER_ACCEPT;
      const element = node as Element;
      const nestedList = unit.localName === "li" && /^(ul|ol)$/.test(element.localName);
      return element.classList.contains(MARK) || element.localName === "svg" || nestedList
        ? NodeFilter.FILTER_REJECT
        : NodeFilter.FILTER_SKIP;
    },
  });
  const nodes: Text[] = [];
  while (walker.nextNode()) nodes.push(walker.currentNode as Text);
  return nodes;
}

const ownText = (unit: Element): string =>
  ownTextNodes(unit)
    .map((node) => node.data)
    .join("");

// Wraps inserted text in <ins> and puts removed text back as a <del> mark, in place, working from the end so
// earlier offsets hold; a removal at the same place goes first, so it reads before its replacement.
function markText(unit: Element, diff: WordDiff): Element[] {
  const marks: Element[] = [];
  const edits = [
    ...diff.deleted.map(({ at, text }) => ({ at, end: at, text })),
    ...diff.inserted.map(({ start, end }) => ({ at: start, end, text: undefined })),
  ].sort((a, b) => b.at - a.at || (a.text === undefined ? 1 : -1));
  for (const { at, end, text } of edits) {
    if (text !== undefined) {
      const del = removedText(text);
      const [node, offset] = locate(unit, at);
      // Past the end, it follows the unit's own last text, ahead of any nested list.
      if (node === undefined) ownTextNodes(unit).at(-1)?.after(del) ?? unit.prepend(del);
      else (offset === 0 ? node : node.splitText(offset)).before(del);
      marks.push(del);
      continue;
    }
    for (const slice of slices(unit, at, end)) {
      const ins = make("ins", "wv-md-ins", "");
      slice.replaceWith(ins);
      ins.append(slice);
      marks.push(ins);
    }
  }
  return marks;
}

// The own text node holding offset `at`, and the offset inside it; none past the end.
function locate(unit: Element, at: number): [Text | undefined, number] {
  let position = 0;
  for (const node of ownTextNodes(unit)) {
    if (at < position + node.data.length) return [node, at - position];
    position += node.data.length;
  }
  return [undefined, 0];
}

// Splits the own text so `[start, end)` is whole text nodes, and returns them.
function slices(unit: Element, start: number, end: number): Text[] {
  const result: Text[] = [];
  let position = 0;
  for (const node of ownTextNodes(unit)) {
    const [from, to] = [position, position + node.data.length];
    position = to;
    if (to <= start || from >= end) continue;
    const slice = start > from ? node.splitText(start - from) : node;
    if (end < to) slice.splitText(end - Math.max(start, from));
    result.push(slice);
  }
  return result;
}
