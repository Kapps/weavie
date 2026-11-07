// Paints a preview's changes: margin bars, removal notches, word marks on the shown change, folds, and the
// header. Everything it adds is tracked and removed on the next paint, so the keyed block nodes stay clean.

import type { BlockList } from "../block-list";
import { ITEM_END, ITEM_START, type LineRange, type MarkdownBlock } from "../markdown-blocks";
import { previewEngine } from "../preview-markdown";
import type { BlockChange } from "./block-changes";
import { type WordDiff, wordDiff } from "./word-diff";

/** Marks are never edit targets; the block editor ignores clicks inside them. */
export const MARK = "wv-md-mark";

export interface MarksState {
  changes: BlockChange[];
  /** "Mostly rewritten" or "New file", when per-block marks would mark nearly everything. */
  rewritten: { label: string } | undefined;
  /** Changes showing word marks: the current one, or all of them. */
  shown: ReadonlySet<BlockChange>;
  changesOnly: boolean;
  baselineBlocks: () => MarkdownBlock[];
  titles: { show: string; changesOnly: string; source: string };
  onShow(change: BlockChange): void;
  onChangesOnly(): void;
  onSource(): void;
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
    this.unmark(this.words.filter((mark) => unit.contains(mark)));
    for (const element of this.added) if (unit.contains(element)) element.remove();
  }

  clear(): void {
    this.unmark(this.words);
    for (const element of this.added) element.remove();
    for (const element of this.classed)
      element.classList.remove("wv-md-changed", "wv-md-added", "wv-md-folded");
    this.words.length = 0;
    this.added.length = 0;
    this.classed.length = 0;
  }

  private unmark(marks: Element[]): void {
    for (const mark of marks) {
      const parent = mark.parentNode;
      if (mark.localName === "ins") mark.replaceWith(...mark.childNodes);
      else mark.remove();
      parent?.normalize();
    }
  }

  paint(state: MarksState): void {
    this.clear();
    if (state.changes.length === 0 && state.rewritten === undefined) return;
    this.header(state);
    if (state.rewritten !== undefined) return;
    const marked = new Set<Element>();
    for (const change of state.changes) {
      const shown = state.shown.has(change);
      for (const { current, baseline } of change.modified) {
        const unit = this.mark(current, "wv-md-changed", change, state, marked);
        if (shown && unit !== undefined) this.markWords(unit, this.oldUnit(baseline, state));
      }
      for (const range of change.added) this.mark(range, "wv-md-added", change, state, marked);
      if (change.removed.length > 0) this.notch(change, shown, state, marked);
    }
    if (state.changesOnly) this.fold(marked);
  }

  private header(state: MarksState): void {
    const bar = this.element("div", "wv-md-header");
    if (state.rewritten !== undefined) {
      bar.append(this.text("span", "wv-md-header-label", state.rewritten.label));
      bar.append(this.button(state.titles.source, "Show diff in Source", state.onSource));
    } else {
      const count = state.changes.length;
      bar.append(
        this.text("span", "wv-md-header-label", `${count} change${count === 1 ? "" : "s"}`),
      );
      const toggle = this.button(state.titles.changesOnly, "Changes only", state.onChangesOnly);
      toggle.setAttribute("aria-pressed", String(state.changesOnly));
      bar.append(toggle);
    }
    this.host.insertBefore(bar, this.body);
  }

  private mark(
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
    const bar = this.button(state.titles.show, "", () => state.onShow(change));
    bar.className = `${MARK} wv-md-bar`;
    bar.setAttribute("aria-label", state.titles.show);
    unit.prepend(bar);
    marked.add(this.topLevel(unit));
    return unit;
  }

  // Removed blocks sit where they were, as a notch, or struck through while their change is shown.
  private notch(
    change: BlockChange,
    shown: boolean,
    state: MarksState,
    marked: Set<Element>,
  ): void {
    const next = this.list.units().find((unit) => this.list.lines(unit)[0] >= change.at);
    const notch = this.element(next?.localName === "li" ? "li" : "div", "wv-md-removed");
    const old = change.removed.map((range) => this.oldUnit(range, state));
    const summary = old
      .map((unit) => unit.textContent ?? "")
      .join(" ")
      .trim();
    notch.append(
      this.button(state.titles.show, `Removed: ${clip(summary)}`, () => state.onShow(change)),
    );
    if (shown) {
      const body = this.element("div", "wv-md-removed-body");
      body.append(...old);
      notch.append(body);
    }
    if (next === undefined) this.body.append(notch);
    else next.before(notch);
    marked.add(this.topLevel(next ?? notch));
  }

  // Folds each run of two or more unmarked top-level blocks behind a row that shows it again.
  private fold(marked: Set<Element>): void {
    const nodes = [...this.body.children].filter((node) => !node.classList.contains(MARK));
    let run: Element[] = [];
    const flush = (): void => {
      const first = run[0];
      if (run.length >= 2 && first !== undefined && !this.expanded.has(first)) {
        const blocks = run;
        for (const node of blocks) {
          node.classList.add("wv-md-folded");
          this.classed.push(node);
        }
        const row = this.element("div", "wv-md-fold");
        row.append(this.text("span", "", `… ${blocks.length} unchanged blocks`));
        row.append(
          this.button("Show these blocks", "Show", () => {
            this.expanded.add(first);
            row.remove();
            for (const node of blocks) node.classList.remove("wv-md-folded");
          }),
        );
        first.before(row);
      }
      run = [];
    };
    for (const node of nodes) {
      if (marked.has(node)) flush();
      else run.push(node);
    }
    flush();
  }

  private markWords(unit: HTMLElement, old: HTMLElement): void {
    if (this.list.blockOf(unit)?.node.hasAttribute("data-md-code")) return;
    const diff = wordDiff(ownText(old), ownText(unit));
    if (diff === null) {
      const note = this.element("span", "wv-md-note");
      note.textContent = "Too many changes to mark words";
      unit.append(note);
      return;
    }
    this.words.push(...markText(unit, diff));
  }

  // The baseline's rendered unit, sanitized like every preview block; never registered, so never editable.
  private oldUnit(range: LineRange, state: MarksState): HTMLElement {
    const block = state.baselineBlocks().findLast((candidate) => candidate.start <= range.start);
    const holder = document.createElement("div");
    if (block === undefined) return holder;
    holder.append(previewEngine.sanitize(block.html));
    const item = holder.querySelector<HTMLElement>(
      `li[${ITEM_START}="${range.start - block.start}"]`,
    );
    // Old content is never a unit, so it keeps no line stamps for the block list to find.
    for (const stamped of holder.querySelectorAll(`[${ITEM_START}]`)) {
      stamped.removeAttribute(ITEM_START);
      stamped.removeAttribute(ITEM_END);
    }
    if (item === null) return holder;
    for (const nested of item.querySelectorAll(":scope > ul, :scope > ol")) nested.remove();
    return item;
  }

  private topLevel(element: Element): Element {
    let node = element;
    while (node.parentElement !== null && node.parentElement !== this.body)
      node = node.parentElement;
    return node;
  }

  private element(tag: string, className: string): HTMLElement {
    const element = document.createElement(tag);
    element.className = `${MARK} ${className}`;
    this.added.push(element);
    return element;
  }

  private text(tag: string, className: string, text: string): HTMLElement {
    const element = document.createElement(tag);
    element.className = className;
    element.textContent = text;
    return element;
  }

  private button(title: string, label: string, run: () => void): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `${MARK} wv-md-action`;
    button.title = title;
    button.textContent = label;
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      run();
    });
    return button;
  }
}

const clip = (text: string): string => (text.length > 60 ? `${text.slice(0, 57)}…` : text);

// The text a unit renders itself: a list item's nested lists and every mark belong to other units.
function ownTextNodes(unit: Element): Text[] {
  const walker = document.createTreeWalker(unit, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
    acceptNode: (node) => {
      if (node instanceof Text)
        return node.data === "" ? NodeFilter.FILTER_SKIP : NodeFilter.FILTER_ACCEPT;
      const element = node as Element;
      if (element.classList.contains(MARK) || element.localName === "svg")
        return NodeFilter.FILTER_REJECT;
      if (element !== unit && unit.localName === "li" && /^(ul|ol)$/.test(element.localName))
        return NodeFilter.FILTER_REJECT;
      return NodeFilter.FILTER_SKIP;
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

// Wraps inserted text in <ins> and puts removed text back as <del>, in place; returns what it added.
function markText(unit: Element, diff: WordDiff): Element[] {
  const marks: Element[] = [];
  const del = (text: string): Element => {
    const element = document.createElement("del");
    element.className = "wv-md-del";
    element.textContent = text;
    marks.push(element);
    return element;
  };
  const nodes = ownTextNodes(unit);
  let total = 0;
  const spans = nodes.map((node) => {
    const span = { node, start: total };
    total += node.data.length;
    return span;
  });
  for (const { node, start } of spans.reverse()) {
    const end = start + node.data.length;
    const cuts = new Set<number>();
    for (const { start: s, end: e } of diff.inserted)
      for (const x of [s, e]) if (x > start && x < end) cuts.add(x);
    for (const { at } of diff.deleted) if (at > start && at < end) cuts.add(at);
    const pieces = [{ node, start }];
    for (const x of [...cuts].sort((a, b) => b - a))
      pieces.splice(1, 0, { node: node.splitText(x - start), start: x });
    for (const piece of pieces) {
      const pieceEnd = piece.start + piece.node.data.length;
      for (const removed of diff.deleted)
        if (removed.at === piece.start) piece.node.before(del(removed.text));
      if (diff.inserted.some((r) => r.start <= piece.start && pieceEnd <= r.end)) {
        const ins = document.createElement("ins");
        ins.className = "wv-md-ins";
        piece.node.replaceWith(ins);
        ins.append(piece.node);
        marks.push(ins);
      }
    }
  }
  const last = nodes.at(-1);
  for (const removed of diff.deleted) if (removed.at === total) last?.after(del(removed.text));
  return marks;
}
