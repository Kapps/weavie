import type { AgentBlockValue, AgentTable } from "../bridge";

/** The column a table is ordered by; null keeps the agent's order. */
export type TableSort = { column: number; descending: boolean } | null;

/** A cell as the table shows it: a time in the user's locale, anything else as written. */
export function cellText(table: AgentTable, column: number, value: AgentBlockValue): string {
  if (value === null) return "";
  return table.columns[column]?.type === "time" && typeof value === "number"
    ? new Date(value).toLocaleString()
    : String(value);
}

/** One column's filter: a cell must contain `phrase` and, when any `values` are picked, equal one of them. */
export interface ColumnFilter {
  phrase: string;
  values: readonly string[];
}

export const noFilter: ColumnFilter = { phrase: "", values: [] };

export function filterActive(filter: ColumnFilter | undefined): boolean {
  return filter !== undefined && (filter.phrase.trim().length > 0 || filter.values.length > 0);
}

export function passesFilter(filter: ColumnFilter, text: string): boolean {
  return (
    (filter.values.length === 0 || filter.values.includes(text)) &&
    text.toLowerCase().includes(filter.phrase.trim().toLowerCase())
  );
}

/** The `limit` most frequent of `texts`, most frequent first, then any of `picked` not among them. */
export function commonValues(
  texts: readonly string[],
  picked: readonly string[],
  limit: number,
): { value: string; count: number }[] {
  const counts = new Map<string, number>();
  for (const text of texts) counts.set(text, (counts.get(text) ?? 0) + 1);
  const common = [...counts]
    .map(([value, count]) => ({ value, count }))
    .sort((left, right) => right.count - left.count)
    .slice(0, limit);
  return [
    ...common,
    ...picked
      .filter((value) => common.every((entry) => entry.value !== value))
      .map((value) => ({ value, count: counts.get(value) ?? 0 })),
  ];
}

/** The filters on `columns` as the agent reads them, e.g. `level is error or warn, message contains "retry"`. */
export function describeFilters(
  columns: readonly string[],
  filters: ReadonlyMap<string, ColumnFilter>,
): string {
  return columns
    .flatMap((column) => {
      const filter = filters.get(column) ?? noFilter;
      return [
        ...(filter.values.length > 0 ? [`${column} is ${filter.values.join(" or ")}`] : []),
        ...(filter.phrase.trim() ? [`${column} contains "${filter.phrase.trim()}"`] : []),
      ];
    })
    .join(", ");
}
