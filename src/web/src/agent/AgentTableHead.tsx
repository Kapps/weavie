import { ArrowDown, ArrowUp, ListFilter } from "lucide-solid";
import { createEffect, createMemo, createSignal, Index, type JSX, Show } from "solid-js";
import type { AgentTable } from "../bridge";
import { AgentTableFilter } from "./AgentTableFilter";
import {
  type ColumnFilter,
  cellText,
  commonValues,
  filterActive,
  noFilter,
  type TableSort,
} from "./agent-table-model";

const commonValueCount = 8;

/** A table's header row: each column sorts from its name and filters from the button beside it. */
export function AgentTableHead(props: {
  table: AgentTable;
  sort: TableSort;
  onSort: (next: (current: TableSort) => TableSort) => void;
  filters: ReadonlyMap<string, ColumnFilter>;
  onFilters: (
    next: (current: ReadonlyMap<string, ColumnFilter>) => Map<string, ColumnFilter>,
  ) => void;
}): JSX.Element {
  const [filtering, setFiltering] = createSignal<{ column: number; anchor: HTMLElement } | null>(
    null,
  );
  const open = createMemo(() => {
    const current = filtering();
    const column = current === null ? undefined : props.table.columns[current.column];
    return current === null || column === undefined ? null : { ...current, name: column.name };
  });
  // A filter whose column an update removed is closed for good, not held for a column that takes its place.
  createEffect(() => {
    if (filtering() !== null && open() === null) setFiltering(null);
  });
  // A time rarely repeats, so a time column filters by phrase alone.
  const common = createMemo(() => {
    const current = open();
    return current === null || props.table.columns[current.column]?.type === "time"
      ? []
      : commonValues(
          props.table.rows.map((row) =>
            cellText(props.table, current.column, row[current.column] ?? null),
          ),
          props.filters.get(current.name)?.values ?? [],
          commonValueCount,
        );
  });
  return (
    <div class="agent-table-head">
      <span class="agent-table-gutter" />
      {/* By index, so an update that resends the same columns keeps each button and an open filter's anchor. */}
      <Index each={props.table.columns}>
        {(column, index) => (
          <span class="agent-table-column">
            <button
              type="button"
              class="ui-button ui-button-content agent-table-sort"
              title={`Sort by ${column().name}`}
              onClick={() =>
                props.onSort((current) =>
                  current?.column !== index
                    ? { column: index, descending: false }
                    : current.descending
                      ? null
                      : { column: index, descending: true },
                )
              }
            >
              {column().name}
              <Show when={props.sort?.column === index}>
                <span class="ui-icon">{props.sort?.descending ? <ArrowDown /> : <ArrowUp />}</span>
              </Show>
            </button>
            <button
              type="button"
              class="ui-icon-button agent-table-filter-toggle"
              title={`Filter ${column().name}`}
              aria-label={`Filter ${column().name}`}
              aria-pressed={filterActive(props.filters.get(column().name))}
              aria-expanded={open()?.column === index}
              onClick={(event) =>
                setFiltering((current) =>
                  current?.column === index ? null : { column: index, anchor: event.currentTarget },
                )
              }
            >
              <ListFilter />
            </button>
          </span>
        )}
      </Index>
      <Show when={open()}>
        {(current) => (
          <AgentTableFilter
            column={current().name}
            anchor={current().anchor}
            filter={props.filters.get(current().name) ?? noFilter}
            common={common()}
            onChange={(filter) =>
              props.onFilters((filters) => new Map(filters).set(current().name, filter))
            }
            onClose={() => setFiltering(null)}
          />
        )}
      </Show>
    </div>
  );
}
