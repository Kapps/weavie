import { createVirtualizer } from "@tanstack/solid-virtual";
import { createMemo, createSignal, For, type JSX } from "solid-js";
import type { AgentBlockValue, AgentTable as AgentTableData } from "../bridge";
import { AgentTableHead } from "./AgentTableHead";
import { blockSelection, rawValue } from "./agent-block-selection";
import {
  type ColumnFilter,
  cellText,
  describeFilters,
  filterActive,
  passesFilter,
  type TableSort,
} from "./agent-table-model";
import type { AgentSelection } from "./composer-state";

const textColumn = "minmax(16ch, 1fr)";
interface Cell {
  row: number;
  column: number;
}
interface Area {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

/** A block table sorted and filtered per column; dragging across cells attaches them, the row number a whole row. */
export function AgentTable(props: {
  id: string;
  table: AgentTableData;
  title: string;
  onSelect: (selection: AgentSelection) => void;
  onSelecting: (active: boolean) => void;
}): JSX.Element {
  let scroller: HTMLDivElement | undefined;
  const [sort, setSort] = createSignal<TableSort>(null);
  // Keyed by column name, so a filter survives the table being replaced with the same columns.
  const [filters, setFilters] = createSignal<ReadonlyMap<string, ColumnFilter>>(new Map());
  const [anchor, setAnchor] = createSignal<Cell | null>(null);
  const [focus, setFocus] = createSignal<Cell | null>(null);

  const rows = createMemo(() => {
    const active = props.table.columns.flatMap((column, index) => {
      const filter = filters().get(column.name);
      return filterActive(filter) ? [{ index, filter: filter! }] : [];
    });
    const shown = props.table.rows.filter((row) =>
      active.every(({ index, filter }) =>
        passesFilter(filter, cellText(props.table, index, row[index] ?? null)),
      ),
    );
    const order = sort();
    if (order !== null) {
      shown.sort(
        (left, right) =>
          compare(left[order.column], right[order.column]) * (order.descending ? -1 : 1),
      );
    }
    return shown;
  });
  // Explicit widths, so the header and the virtualized rows line up without sharing one grid: fixed columns take
  // their longest value, text columns share the rest, and the table never gets narrower than all of them at minimum.
  const columns = createMemo(() => {
    const gutter = String(props.table.rows.length).length + 2;
    const widths = props.table.columns.map((column, index) => {
      if (column.type === "text") return textColumn;
      const values = column.type === "time" ? props.table.rows.slice(0, 1) : props.table.rows;
      const longest = values.reduce(
        (width, row) => Math.max(width, cellText(props.table, index, row[index] ?? null).length),
        column.name.length + 5,
      );
      return Math.min(longest, 32) + 2;
    });
    const minimum =
      gutter +
      widths.reduce<number>((sum, width) => sum + (typeof width === "number" ? width : 16), 0);
    return {
      template: `${gutter}ch ${widths.map((width) => (typeof width === "number" ? `${width}ch` : width)).join(" ")}`,
      minWidth: `calc(${minimum}ch + ${props.table.columns.length * 12}px)`,
    };
  });

  const virtualizer = createVirtualizer<HTMLDivElement, HTMLDivElement>({
    get count() {
      return rows().length;
    },
    // For rows not yet measured; a mounted row reports its own height.
    estimateSize: () => 25,
    getScrollElement: () => scroller ?? null,
    overscan: 8,
  });

  const area = createMemo((): Area | null => {
    const from = anchor();
    const to = focus();
    return from === null || to === null
      ? null
      : {
          top: Math.min(from.row, to.row),
          bottom: Math.max(from.row, to.row),
          left: Math.min(from.column, to.column),
          right: Math.max(from.column, to.column),
        };
  });
  const selected = (row: number, column: number): boolean => {
    const box = area();
    return (
      box !== null &&
      row >= box.top &&
      row <= box.bottom &&
      column >= box.left &&
      column <= box.right
    );
  };
  const value = (row: number, column: number): AgentBlockValue => rows()[row]?.[column] ?? null;

  const begin = (from: Cell, to: Cell, event: PointerEvent): void => {
    if (event.button !== 0) return;
    setAnchor(from);
    setFocus(to);
    props.onSelecting(true);
    scroller?.setPointerCapture(event.pointerId);
  };
  const extend = (event: PointerEvent): void => {
    if (anchor() === null) return;
    const cell = (
      document.elementFromPoint(event.clientX, event.clientY) as HTMLElement | null
    )?.closest<HTMLElement>("[data-cell]");
    if (cell?.dataset.cell === undefined) return;
    const [row, column] = cell.dataset.cell.split(":").map(Number);
    setFocus({ row: row!, column: column ?? props.table.columns.length - 1 });
  };
  const stop = (): void => {
    setAnchor(null);
    setFocus(null);
    props.onSelecting(false);
  };
  const end = (): void => {
    const box = area();
    stop();
    if (box !== null) props.onSelect(selectionOf(box));
  };

  const selectionOf = (box: Area): AgentSelection => {
    const picked = props.table.columns.slice(box.left, box.right + 1);
    const span =
      box.top === box.bottom ? `row ${box.top + 1}` : `rows ${box.top + 1}-${box.bottom + 1}`;
    const where = describeFilters(
      props.table.columns.map((column) => column.name),
      filters(),
    );
    return blockSelection(
      "Table",
      { id: props.id, title: props.title },
      { label: span, detail: `${span} of ${rows().length}${where ? ` where ${where}` : ""}` },
      picked.map((column) => column.name),
      rows()
        .slice(box.top, box.bottom + 1)
        .map((row) =>
          picked.map((column, index) => rawValue(column.type, row[box.left + index] ?? null)),
        ),
    );
  };

  return (
    <div
      class="agent-table"
      style={{
        "--agent-table-columns": columns().template,
        "--agent-table-min-width": columns().minWidth,
        "--agent-table-count": props.table.rows.length,
      }}
    >
      <div
        class="agent-table-body"
        ref={scroller}
        onPointerMove={extend}
        onPointerUp={end}
        onPointerCancel={stop}
      >
        <AgentTableHead
          table={props.table}
          sort={sort()}
          onSort={setSort}
          filters={filters()}
          onFilters={setFilters}
        />
        <div class="agent-table-rows" style={{ height: `${virtualizer.getTotalSize()}px` }}>
          <For each={virtualizer.getVirtualItems()}>
            {(item) => (
              <div
                class="ui-row ui-row-content agent-table-row"
                data-index={item.index}
                ref={(element) =>
                  queueMicrotask(() => element.isConnected && virtualizer.measureElement(element))
                }
                style={{ transform: `translateY(${item.start}px)` }}
              >
                <span
                  class="agent-table-gutter"
                  data-cell={`${item.index}`}
                  onPointerDown={(event) =>
                    begin(
                      { row: item.index, column: 0 },
                      { row: item.index, column: props.table.columns.length - 1 },
                      event,
                    )
                  }
                >
                  {item.index + 1}
                </span>
                <For each={props.table.columns}>
                  {(column, index) => {
                    const text = () => cellText(props.table, index(), value(item.index, index()));
                    return (
                      <span
                        class="agent-table-cell"
                        classList={{
                          "agent-table-selected": selected(item.index, index()),
                          "agent-table-number": column.type === "number",
                        }}
                        title={text()}
                        data-level={
                          column.type === "level" ? level(value(item.index, index())) : undefined
                        }
                        data-cell={`${item.index}:${index()}`}
                        onPointerDown={(event) => {
                          const cell = { row: item.index, column: index() };
                          begin(cell, cell, event);
                        }}
                      >
                        {text()}
                      </span>
                    );
                  }}
                </For>
              </div>
            )}
          </For>
        </div>
      </div>
    </div>
  );
}

function compare(left: AgentBlockValue | undefined, right: AgentBlockValue | undefined): number {
  if (left === right) return 0;
  if (left === null || left === undefined) return 1;
  if (right === null || right === undefined) return -1;
  return typeof left === "number" && typeof right === "number"
    ? left - right
    : String(left).localeCompare(String(right));
}

function level(value: AgentBlockValue): string {
  const name = String(value ?? "").toLowerCase();
  if (/^(error|err|fatal|critical|crit|alert|emerg)/.test(name)) return "bad";
  if (/^warn/.test(name)) return "warn";
  if (/^(debug|trace|verbose)/.test(name)) return "dim";
  return "fg";
}
