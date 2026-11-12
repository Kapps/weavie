import { createVirtualizer } from "@tanstack/solid-virtual";
import { ArrowDown, ArrowUp } from "lucide-solid";
import { createMemo, createSignal, For, type JSX, Show } from "solid-js";
import type { AgentBlockValue, AgentTable as AgentTableData } from "../bridge";
import type { BlockSelection } from "./agent-block-selection";

type Sort = { column: number; descending: boolean } | null;
interface Cell {
  row: number;
  column: number;
}

/** A sortable, filterable block table; dragging across cells attaches them, the row number a whole row. */
export function AgentTable(props: {
  id: string;
  table: AgentTableData;
  title: string;
  onSelect: (selection: BlockSelection) => void;
  onSelecting: (active: boolean) => void;
}): JSX.Element {
  let scroller: HTMLDivElement | undefined;
  const [sort, setSort] = createSignal<Sort>(null);
  const [filter, setFilter] = createSignal("");
  const [anchor, setAnchor] = createSignal<Cell | null>(null);
  const [focus, setFocus] = createSignal<Cell | null>(null);

  const rows = createMemo(() => {
    const query = filter().trim().toLowerCase();
    const shown = props.table.rows.filter(
      (row) =>
        query.length === 0 ||
        row.some((value, column) =>
          cellText(props.table, column, value).toLowerCase().includes(query),
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
  // Explicit widths, so the header and the virtualized rows line up without sharing one grid.
  const columns = createMemo(() => {
    const widths = props.table.columns.map((column, index) => {
      if (column.type === "text") return "minmax(16ch, 1fr)";
      const values = column.type === "time" ? props.table.rows.slice(0, 1) : props.table.rows;
      const longest = values.reduce(
        (width, row) => Math.max(width, cellText(props.table, index, row[index] ?? null).length),
        column.name.length + 2,
      );
      return `${Math.min(longest, 32) + 2}ch`;
    });
    return `${String(props.table.rows.length).length + 2}ch ${widths.join(" ")}`;
  });

  const virtualizer = createVirtualizer<HTMLDivElement, HTMLDivElement>({
    get count() {
      return rows().length;
    },
    estimateSize: () => 24,
    getScrollElement: () => scroller ?? null,
    overscan: 8,
  });

  const selected = (row: number, column: number): boolean => {
    const from = anchor();
    const to = focus();
    if (from === null || to === null) return false;
    return (
      row >= Math.min(from.row, to.row) &&
      row <= Math.max(from.row, to.row) &&
      column >= Math.min(from.column, to.column) &&
      column <= Math.max(from.column, to.column)
    );
  };

  const begin = (cell: Cell, event: PointerEvent): void => {
    if (event.button !== 0) return;
    setAnchor(cell);
    setFocus(cell);
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
  const end = (): void => {
    const from = anchor();
    const to = focus();
    setAnchor(null);
    setFocus(null);
    props.onSelecting(false);
    if (from !== null && to !== null) props.onSelect(selectionOf(from, to));
  };

  const selectionOf = (from: Cell, to: Cell): BlockSelection => {
    const top = Math.min(from.row, to.row);
    const bottom = Math.max(from.row, to.row);
    const left = Math.min(from.column, to.column);
    const right = Math.max(from.column, to.column);
    const picked = props.table.columns.slice(left, right + 1);
    const span = top === bottom ? `row ${top + 1}` : `rows ${top + 1}-${bottom + 1}`;
    return {
      label: `${props.title} · ${span}`,
      text: [
        `Table "${props.title}" (${props.id}), ${span} of ${rows().length}${filter().trim() ? ` matching "${filter().trim()}"` : ""}`,
        picked.map((column) => csv(column.name)).join(","),
        ...rows()
          .slice(top, bottom + 1)
          .map((row) =>
            picked
              .map((column, index) => csv(rawText(column.type, row[left + index] ?? null)))
              .join(","),
          ),
      ].join("\n"),
    };
  };

  return (
    <div class="agent-table" style={{ "--agent-table-columns": columns() }}>
      <input
        class="ui-field ui-field-content agent-table-filter"
        type="search"
        placeholder="Filter"
        aria-label={`Filter ${props.title}`}
        value={filter()}
        onInput={(event) => setFilter(event.currentTarget.value)}
      />
      <div class="agent-table-head">
        <span class="agent-table-gutter" />
        <For each={props.table.columns}>
          {(column, index) => (
            <button
              type="button"
              class="ui-button ui-button-content agent-table-sort"
              title={`Sort by ${column.name}`}
              onClick={() =>
                setSort((current) =>
                  current?.column !== index()
                    ? { column: index(), descending: false }
                    : current.descending
                      ? null
                      : { column: index(), descending: true },
                )
              }
            >
              {column.name}
              <Show when={sort()?.column === index()}>
                <span class="ui-icon">{sort()?.descending ? <ArrowDown /> : <ArrowUp />}</span>
              </Show>
            </button>
          )}
        </For>
      </div>
      <div
        class="agent-table-body"
        ref={scroller}
        onPointerMove={extend}
        onPointerUp={end}
        onPointerCancel={() => {
          setAnchor(null);
          props.onSelecting(false);
        }}
      >
        <div style={{ height: `${virtualizer.getTotalSize()}px`, position: "relative" }}>
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
                  onPointerDown={(event) => {
                    begin({ row: item.index, column: 0 }, event);
                    setFocus({ row: item.index, column: props.table.columns.length - 1 });
                  }}
                >
                  {item.index + 1}
                </span>
                <For each={props.table.columns}>
                  {(column, index) => (
                    <span
                      class="agent-table-cell"
                      classList={{
                        "agent-table-selected": selected(item.index, index()),
                        "agent-table-number": column.type === "number",
                      }}
                      data-level={
                        column.type === "level"
                          ? level(rows()[item.index]?.[index()] ?? null)
                          : undefined
                      }
                      data-cell={`${item.index}:${index()}`}
                      onPointerDown={(event) => begin({ row: item.index, column: index() }, event)}
                    >
                      {cellText(props.table, index(), rows()[item.index]?.[index()] ?? null)}
                    </span>
                  )}
                </For>
              </div>
            )}
          </For>
        </div>
      </div>
    </div>
  );
}

function cellText(table: AgentTableData, column: number, value: AgentBlockValue): string {
  if (value === null) return "";
  return table.columns[column]?.type === "time" && typeof value === "number"
    ? new Date(value).toLocaleString()
    : String(value);
}

function rawText(type: string, value: AgentBlockValue): string {
  if (value === null) return "";
  return type === "time" && typeof value === "number"
    ? new Date(value).toISOString()
    : String(value);
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

function csv(value: string): string {
  return /[",\n]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value;
}
