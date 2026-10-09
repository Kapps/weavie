// Interactive cards between an editor's lines. Each card reserves its height with an empty view zone and renders
// in a layer beside `.monaco-editor`, so it owns real focus and keyboard input (a view zone can't) and stays the
// width of the visible code instead of its longest line.

import { type Accessor, createEffect, createSignal, For, type JSX, onCleanup } from "solid-js";
import { render } from "solid-js/web";
import { monaco } from "./monaco-setup";
import "./zone-cards.css";

const CARD_MAX_WIDTH = 760;
const CARD_INSET = 12;

export interface ZoneCard<T> {
  key: string;
  afterLine: number;
  data: T;
}

interface Slot<T> {
  zoneId: string;
  zone: monaco.editor.IViewZone & { heightInPx: number };
  top: Accessor<number>;
  data: Accessor<T>;
  setData(data: T): void;
}

/** Keeps one card per key below its line; must be created inside a reactive owner. */
export function createZoneCards<T>(
  editor: monaco.editor.ICodeEditor,
  cards: Accessor<ZoneCard<T>[]>,
  renderCard: (data: Accessor<T>) => JSX.Element,
  bottomInset: () => number,
): () => void {
  const slots = new Map<string, Slot<T>>();
  const [list, setList] = createSignal<Slot<T>[]>([]);
  const [layout, setLayout] = createSignal(editor.getLayoutInfo());
  const onLayout = editor.onDidLayoutChange(setLayout);

  createEffect(() => {
    const next = new Map(cards().map((card) => [card.key, card]));
    editor.changeViewZones((accessor) => {
      for (const [key, slot] of slots) {
        if (next.has(key)) continue;
        accessor.removeZone(slot.zoneId);
        slots.delete(key);
      }
      for (const [key, card] of next) {
        const existing = slots.get(key);
        if (existing !== undefined) {
          existing.setData(card.data);
          if (existing.zone.afterLineNumber !== card.afterLine) {
            existing.zone.afterLineNumber = card.afterLine;
            accessor.layoutZone(existing.zoneId);
          }
          continue;
        }
        const [top, setTop] = createSignal(Number.NEGATIVE_INFINITY);
        const [data, setData] = createSignal(card.data);
        const zone = {
          afterLineNumber: card.afterLine,
          heightInPx: 0,
          domNode: document.createElement("div"),
          onDomNodeTop: setTop,
        };
        const zoneId = accessor.addZone(zone);
        slots.set(key, { zoneId, zone, top, data, setData: (value) => setData(() => value) });
      }
    });
    setList([...slots.values()]);
  });

  // Keeps the card holding focus fully in view — clear of whatever the caller says floats over the editor's
  // bottom edge — by scrolling the editor, the only thing that moves cards and code together.
  const reveal = (slot: Slot<T>, node: HTMLElement): void => {
    if (!node.contains(document.activeElement)) return;
    const top = slot.top();
    const overflow = top + node.offsetHeight - (editor.getLayoutInfo().height - bottomInset());
    const scroll = top < 0 ? top : Math.max(0, Math.min(overflow, top));
    if (scroll !== 0) editor.setScrollTop(editor.getScrollTop() + scroll);
  };

  const measured = (slot: Slot<T>) => (node: HTMLElement) => {
    const observer = new ResizeObserver(() => {
      reveal(slot, node);
      if (Math.abs(slot.zone.heightInPx - node.offsetHeight) < 1) return;
      slot.zone.heightInPx = node.offsetHeight;
      editor.changeViewZones((accessor) => accessor.layoutZone(slot.zoneId));
    });
    node.addEventListener("focusin", () => requestAnimationFrame(() => reveal(slot, node)));
    observer.observe(node);
    onCleanup(() => observer.disconnect());
  };

  const layer = document.createElement("div");
  layer.className = "weavie-zone-cards";
  // Monaco's keybinding service listens on the editor container; typing in a card is not editor input.
  for (const type of ["keydown", "keypress", "keyup"]) {
    layer.addEventListener(type, (event) => event.stopPropagation());
  }
  // The layer sits outside Monaco's scrollable element, so a wheel over a card scrolls the code explicitly —
  // unless something in the card (a long draft, a wide code block) can scroll that way itself.
  layer.addEventListener(
    "wheel",
    (event) => {
      const scale =
        event.deltaMode === WheelEvent.DOM_DELTA_LINE
          ? editor.getOption(monaco.editor.EditorOption.lineHeight)
          : 1;
      // Shift turns a vertical wheel sideways, as it does over the code.
      const [dx, dy] =
        event.shiftKey && event.deltaX === 0 ? [event.deltaY, 0] : [event.deltaX, event.deltaY];
      if (scrollsItself(event.target as Element, dx, dy)) return;
      editor.setScrollPosition({
        scrollTop: editor.getScrollTop() + dy * scale,
        scrollLeft: editor.getScrollLeft() + dx * scale,
      });
    },
    { passive: true },
  );
  const scrollsItself = (target: Element, dx: number, dy: number): boolean => {
    for (let el: Element | null = target; el !== null && el !== layer; el = el.parentElement) {
      // Only a real scroll container counts: clipped content (a folded body, an ellipsis) can't be scrolled.
      const style = getComputedStyle(el);
      const scrolls = (overflow: string) => overflow === "auto" || overflow === "scroll";
      const canY =
        scrolls(style.overflowY) &&
        ((dy < 0 && el.scrollTop > 0) ||
          (dy > 0 && el.scrollTop + el.clientHeight < el.scrollHeight));
      const canX =
        scrolls(style.overflowX) &&
        ((dx < 0 && el.scrollLeft > 0) ||
          (dx > 0 && el.scrollLeft + el.clientWidth < el.scrollWidth));
      if (canY || canX) return true;
    }
    return false;
  };
  const container = editor.getContainerDomNode();
  container.insertBefore(layer, container.firstChild);
  const unmount = render(
    () => (
      <div
        class="weavie-zone-cards-clip"
        style={{ width: `${layout().width}px`, height: `${layout().height}px` }}
      >
        <For each={list()}>
          {(slot) => (
            <div
              ref={measured(slot)}
              class="weavie-zone-card"
              style={{
                top: `${slot.top()}px`,
                visibility: Number.isFinite(slot.top()) ? "visible" : "hidden",
                left: `${layout().contentLeft + CARD_INSET}px`,
                width: `${Math.max(0, Math.min(CARD_MAX_WIDTH, layout().contentWidth - layout().verticalScrollbarWidth - 2 * CARD_INSET))}px`,
              }}
            >
              {renderCard(slot.data)}
            </div>
          )}
        </For>
      </div>
    ),
    layer,
  );

  return () => {
    onLayout.dispose();
    unmount();
    layer.remove();
    editor.changeViewZones((accessor) => {
      for (const slot of slots.values()) accessor.removeZone(slot.zoneId);
    });
  };
}
