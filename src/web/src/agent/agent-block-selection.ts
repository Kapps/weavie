import { createSignal } from "solid-js";
import type { AgentBlock, AgentBlockValue, AgentChart } from "../bridge";
import type { AgentSelection } from "./composer-state";

// The block row a gesture is in progress on stays mounted until the gesture ends.
const [selecting, setSelecting] = createSignal<string | null>(null);
export const selectingBlockEntry = selecting;

export function noteBlockSelecting(entryId: string, active: boolean): void {
  setSelecting((current) => (active ? entryId : current === entryId ? null : current));
}

/** A value as the agent receives it: a time as ISO 8601, anything else as written. */
export function rawValue(type: string | null | undefined, value: AgentBlockValue): string {
  if (value === null) return "";
  return type === "time" && typeof value === "number"
    ? new Date(value).toISOString()
    : String(value);
}

// A value in a chip label, where a full timestamp would push the rest of the label out of view.
function shortValue(type: string | null | undefined, value: AgentBlockValue): string {
  return type === "time" && typeof value === "number"
    ? new Date(value).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
    : rawValue(type, value);
}

/** A block selection: a one-line chip label, and the CSV of what was selected under a line naming the block. */
export function blockSelection(
  kind: "Chart" | "Table",
  block: { id: string; title: string },
  scope: { label: string; detail: string },
  header: readonly string[],
  rows: readonly (readonly string[])[],
): AgentSelection {
  return {
    label: `${block.title} · ${scope.label}`,
    text: [
      `${kind} "${block.title}" (${block.id}), ${scope.detail}`,
      ...[header, ...rows].map(csvLine),
    ].join("\n"),
  };
}

/** The points of `chart` a brush from `from` to `to` covers; a category axis reports category indices. */
export function chartRangeSelection(
  block: { id: string; title: string },
  chart: AgentChart,
  from: number,
  to: number,
): AgentSelection {
  const xs = chartXs(chart);
  const covered =
    chart.x.type === "category"
      ? xs.slice(Math.max(0, Math.ceil(from)), Math.floor(to) + 1)
      : xs.filter((x) => typeof x === "number" && x >= from && x <= to);
  const ends =
    chart.x.type === "category" ? [covered[0] ?? null, covered.at(-1) ?? null] : [from, to];
  const range = (format: typeof rawValue): string =>
    `${format(chart.x.type, ends[0] ?? null)} to ${format(chart.x.type, ends[1] ?? null)}`;
  return chartRows(block, chart, covered, { label: range(shortValue), detail: range(rawValue) });
}

// Every distinct x, in category order for a category axis and ascending otherwise; a null x has no position.
function chartXs(chart: AgentChart): AgentBlockValue[] {
  const xs = [
    ...new Set(chart.series.flatMap((line) => line.points.map((point) => point[0]))),
  ].filter((x) => x !== null);
  return chart.x.type === "category" ? xs : xs.sort((left, right) => Number(left) - Number(right));
}

function chartRows(
  block: { id: string; title: string },
  chart: AgentChart,
  xs: readonly AgentBlockValue[],
  scope: { label: string; detail: string },
): AgentSelection {
  return blockSelection(
    "Chart",
    block,
    scope,
    [chart.x.label ?? "x", ...chart.series.map((line) => line.name)],
    xs.map((x) => [
      rawValue(chart.x.type, x),
      ...chart.series.map((line) =>
        rawValue("number", line.points.find((point) => point[0] === x)?.[1] ?? null),
      ),
    ]),
  );
}

/** One point of one series. */
export function chartPointSelection(
  block: { id: string; title: string },
  chart: AgentChart,
  series: string,
  point: [AgentBlockValue, AgentBlockValue],
): AgentSelection {
  return blockSelection(
    "Chart",
    block,
    {
      label: `${series} at ${shortValue(chart.x.type, point[0])}`,
      detail: `series "${series}" at ${rawValue(chart.x.type, point[0])}`,
    },
    [chart.x.label ?? "x", series],
    [[rawValue(chart.x.type, point[0]), rawValue("number", point[1])]],
  );
}

/** All of a block's data, for attaching the whole block from the keyboard. */
export function wholeBlockSelection(block: AgentBlock): AgentSelection {
  if (block.chart != null) {
    const xs = chartXs(block.chart);
    const scope = `all ${xs.length} points`;
    return chartRows(block, block.chart, xs, { label: scope, detail: scope });
  }
  const table = block.table!;
  return blockSelection(
    "Table",
    block,
    { label: `all ${table.rows.length} rows`, detail: `all ${table.rows.length} rows` },
    table.columns.map((column) => column.name),
    table.rows.map((row) => row.map((value, index) => rawValue(table.columns[index]?.type, value))),
  );
}

function csvLine(values: readonly string[]): string {
  return values
    .map((value) => (/[",\n]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value))
    .join(",");
}
