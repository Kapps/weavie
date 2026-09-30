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

  const measured = (slot: Slot<T>) => (node: HTMLElement) => {
    const observer = new ResizeObserver(() => {
      if (Math.abs(slot.zone.heightInPx - node.offsetHeight) < 1) return;
      slot.zone.heightInPx = node.offsetHeight;
      editor.changeViewZones((accessor) => accessor.layoutZone(slot.zoneId));
    });
    observer.observe(node);
    onCleanup(() => observer.disconnect());
  };

  const layer = document.createElement("div");
  layer.className = "weavie-zone-cards";
  // Monaco's keybinding service listens on the editor container; typing in a card is not editor input.
  for (const type of ["keydown", "keypress", "keyup"]) {
    layer.addEventListener(type, (event) => event.stopPropagation());
  }
  // The layer sits outside Monaco's scrollable element, so a wheel over a card scrolls the code explicitly.
  layer.addEventListener(
    "wheel",
    (event) => {
      const scale =
        event.deltaMode === WheelEvent.DOM_DELTA_LINE
          ? editor.getOption(monaco.editor.EditorOption.lineHeight)
          : 1;
      editor.setScrollPosition({
        scrollTop: editor.getScrollTop() + event.deltaY * scale,
        scrollLeft: editor.getScrollLeft() + event.deltaX * scale,
      });
    },
    { passive: true },
  );
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
