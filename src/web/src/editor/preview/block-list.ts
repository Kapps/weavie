import { ITEM_END, ITEM_START, type MarkdownBlock } from "./markdown-blocks";
import { previewEngine } from "./preview-markdown";

const ITEM = `li[${ITEM_START}]`;

/** A rendered top-level block. Line ranges live here, never in attributes a document could forge. */
export interface RenderedBlock {
  html: string;
  node: HTMLElement;
  start: number;
  end: number;
}

/** The preview's top-level blocks: their nodes, line ranges, and the units a user can edit. */
export class BlockList {
  private items: RenderedBlock[] = [];
  private readonly byNode = new WeakMap<Element, RenderedBlock>();

  constructor(
    private readonly body: HTMLElement,
    private readonly title: () => string,
  ) {}

  /** Shows `blocks` plus `held`, reusing every node whose HTML is unchanged (none when `rebuild`). */
  reconcile(blocks: MarkdownBlock[], held: RenderedBlock | undefined, rebuild: boolean): void {
    const pool = rebuild ? [] : this.items.filter((item) => item !== held);
    const next: RenderedBlock[] = [];
    for (const block of blocks) {
      const index = pool.findIndex((item) => item.html === block.html);
      const item = index === -1 ? this.create(block) : (pool.splice(index, 1)[0] as RenderedBlock);
      item.start = block.start;
      item.end = block.end;
      next.push(item);
    }
    this.items = next;
    if (held === undefined) this.place();
    else this.insert(held);
  }

  /** Adds an empty block on `line`. */
  addEmpty(line: number): RenderedBlock {
    const block = this.create({ start: line, end: line + 1, html: "", code: false });
    this.insert(block);
    return block;
  }

  remove(block: RenderedBlock): void {
    this.items = this.items.filter((item) => item !== block);
    block.node.remove();
  }

  /** The editable unit around `target`: its list item, else its top-level block. */
  unitFrom(target: Element): HTMLElement | undefined {
    return target.closest<HTMLElement>(ITEM) ?? this.blockOf(target)?.node;
  }

  /** Whether `element` is itself an editable unit. */
  isUnit(element: Element): boolean {
    return element.matches(ITEM) || this.byNode.has(element);
  }

  blockOf(element: Element): RenderedBlock | undefined {
    let node: Element | null = element;
    while (node !== null && node.parentElement !== this.body) node = node.parentElement;
    return node === null ? undefined : this.byNode.get(node);
  }

  /** The 0-based `[start, end)` lines of `unit`. */
  lines(unit: HTMLElement): [number, number] {
    const block = this.blockOf(unit) as RenderedBlock;
    return unit === block.node
      ? [block.start, block.end]
      : [
          block.start + Number(unit.getAttribute(ITEM_START)),
          block.start + Number(unit.getAttribute(ITEM_END)),
        ];
  }

  /** Every unit in document order, with a list standing as its items. */
  units(): HTMLElement[] {
    return this.items.flatMap(({ node }) => {
      const items = [...node.querySelectorAll<HTMLElement>(ITEM)];
      return items.length > 0 ? items : [node];
    });
  }

  unitAt(line: number): HTMLElement | undefined {
    return this.units().find((unit) => this.lines(unit)[0] === line);
  }

  /** The innermost unit holding `line`, or the last one before it. */
  unitNear(line: number): HTMLElement | undefined {
    return this.units().findLast((unit) => this.lines(unit)[0] <= line);
  }

  lastEnd(): number {
    return this.items.at(-1)?.end ?? 0;
  }

  private insert(block: RenderedBlock): void {
    const index = this.items.findIndex((item) => item.start >= block.start);
    this.items.splice(index === -1 ? this.items.length : index, 0, block);
    this.place();
  }

  private create(block: MarkdownBlock): RenderedBlock {
    const node = document.createElement("div");
    node.append(previewEngine.sanitize(block.html));
    node.toggleAttribute("data-md-code", block.code);
    const title = this.title();
    for (const unit of [node, ...node.querySelectorAll<HTMLElement>(ITEM)]) {
      unit.tabIndex = 0;
      unit.title = title;
      unit.classList.add("md-unit");
    }
    const rendered = { html: block.html, node, start: block.start, end: block.end };
    this.byNode.set(node, rendered);
    return rendered;
  }

  // Moves only what changed, so an open textarea keeps its focus and hydrated diagrams stay put.
  private place(): void {
    const keep = new Set<Element>(this.items.map((item) => item.node));
    for (const child of [...this.body.children]) if (!keep.has(child)) child.remove();
    let cursor = this.body.firstElementChild;
    for (const { node } of this.items) {
      if (node === cursor) cursor = cursor.nextElementSibling;
      else this.body.insertBefore(node, cursor);
    }
  }
}
