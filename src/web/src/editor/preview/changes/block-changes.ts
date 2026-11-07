// Groups a file's pending line hunks into whole rendered blocks, so the preview reviews what it shows. Pure.

import type { DiffHunk } from "../../review/diff-markers";
import type { LineRange } from "../markdown-blocks";

/** One reviewable change: whole units on both sides, plus the block-aligned hunk Keep and Revert act on. */
export interface BlockChange {
  hunk: DiffHunk;
  /** Changed units, paired current with baseline. */
  modified: { current: LineRange; baseline: LineRange }[];
  /** Current units with no baseline counterpart. */
  added: LineRange[];
  /** Baseline units with no current counterpart, shown before current line `at`. */
  removed: LineRange[];
  at: number;
}

interface Span {
  cs: number;
  ce: number;
  bs: number;
  be: number;
}

type Side = "current" | "baseline";

/**
 * `hunks` are the diff's line hunks (1-based, end-exclusive); `current`/`baseline` are each side's units and
 * source lines. Every line hunk lands in exactly one change, so keeping every change keeps the whole file.
 */
export function blockChanges(
  hunks: readonly DiffHunk[],
  current: { units: LineRange[]; lines: string[] },
  baseline: { units: LineRange[]; lines: string[] },
): BlockChange[] {
  const line = hunks.map((h) => ({
    cs: h.currentStart - 1,
    ce: h.currentEndExclusive - 1,
    bs: h.baselineStart - 1,
    be: h.baselineEndExclusive - 1,
  }));
  const units = { current: current.units, baseline: baseline.units };
  const across: Across = (from, x, edge) => mapLine(line, from, x, edge);
  let spans = line.map((span) => ({ ...span }));
  let changed = true;
  while (changed) {
    changed = false;
    for (const span of spans) {
      changed = grow(span, "current", units.current, across) || changed;
      changed = grow(span, "baseline", units.baseline, across) || changed;
      changed = adopt(span, units, across) || changed;
    }
    const merged = merge(spans);
    changed = changed || merged.length !== spans.length;
    spans = merged;
  }
  return spans.map((span) => describe(span, current, baseline));
}

const touches = (unit: LineRange, start: number, end: number): boolean =>
  start === end ? unit.start < start && start < unit.end : unit.start < end && start < unit.end;

const KEYS = ["cs", "ce", "bs", "be"] as const;

type Across = (from: Side, x: number, edge: "start" | "end") => number;

// Widens a span to every unit it touches on one side.
function grow(span: Span, side: Side, units: LineRange[], across: Across): boolean {
  let [start, end] = side === "current" ? [span.cs, span.ce] : [span.bs, span.be];
  for (const unit of units) {
    if (!touches(unit, start, end)) continue;
    start = Math.min(start, unit.start);
    end = Math.max(end, unit.end);
  }
  return widen(span, side, start, end, across);
}

// A change touching no unit on either side (blank lines, link definitions) joins the next unit, else the last.
function adopt(span: Span, units: Record<Side, LineRange[]>, across: Across): boolean {
  const covers = (list: LineRange[], s: number, e: number) =>
    list.some((u) => u.start < e && s < u.end);
  if (covers(units.current, span.cs, span.ce) || covers(units.baseline, span.bs, span.be))
    return false;
  const host =
    units.current.find((u) => u.start >= span.ce) ??
    units.current.findLast((u) => u.end <= span.cs);
  return host !== undefined && widen(span, "current", host.start, host.end, across);
}

// Takes `[start, end)` into the span on one side, and its edges, mapped across unchanged lines, on the other.
function widen(span: Span, side: Side, start: number, end: number, across: Across): boolean {
  const before = { ...span };
  const [s, e, os, oe] =
    side === "current" ? KEYS : ([...KEYS.slice(2), ...KEYS.slice(0, 2)] as const);
  span[s] = Math.min(span[s], start);
  span[e] = Math.max(span[e], end);
  span[os] = Math.min(span[os], across(side, span[s], "start"));
  span[oe] = Math.max(span[oe], across(side, span[e], "end"));
  return KEYS.some((key) => span[key] !== before[key]);
}

function merge(spans: Span[]): Span[] {
  const sorted = [...spans].sort((a, b) => a.cs - b.cs || a.bs - b.bs);
  const result: Span[] = [];
  for (const span of sorted) {
    const last = result.at(-1);
    if (last !== undefined && (span.cs < last.ce || span.bs < last.be)) {
      last.cs = Math.min(last.cs, span.cs);
      last.ce = Math.max(last.ce, span.ce);
      last.bs = Math.min(last.bs, span.bs);
      last.be = Math.max(last.be, span.be);
    } else result.push({ ...span });
  }
  return result;
}

// Maps a line boundary across the diff: unchanged lines move by the offset of the hunks above them.
function mapLine(hunks: Span[], from: Side, x: number, edge: "start" | "end"): number {
  let offset = 0;
  for (const h of hunks) {
    const [fs, fe, ts, te] =
      from === "current" ? [h.cs, h.ce, h.bs, h.be] : [h.bs, h.be, h.cs, h.ce];
    if (x <= fs) break;
    if (x < fe) return edge === "start" ? ts : te;
    offset = te - fe;
  }
  return x + offset;
}

function describe(
  span: Span,
  current: { units: LineRange[]; lines: string[] },
  baseline: { units: LineRange[]; lines: string[] },
): BlockChange {
  const within = (units: LineRange[], s: number, e: number) =>
    units.filter((u) => u.start >= s && u.end <= e);
  const text = (lines: string[], u: LineRange) => lines.slice(u.start, u.end).join("\n");
  const now = within(current.units, span.cs, span.ce);
  const was = within(baseline.units, span.bs, span.be);
  // Units pulled in whole but unchanged (an adopted neighbor) carry no mark.
  while (now[0] && was[0] && text(current.lines, now[0]) === text(baseline.lines, was[0])) {
    now.shift();
    was.shift();
  }
  while (
    now.at(-1) &&
    was.at(-1) &&
    text(current.lines, now.at(-1)!) === text(baseline.lines, was.at(-1)!)
  ) {
    now.pop();
    was.pop();
  }
  const paired = Math.min(now.length, was.length);
  const anchor = now[0]?.start ?? span.cs;
  return {
    hunk: {
      anchorLine: Math.min(anchor, Math.max(current.lines.length - 1, 0)) + 1,
      currentStart: span.cs + 1,
      currentEndExclusive: span.ce + 1,
      baselineStart: span.bs + 1,
      baselineEndExclusive: span.be + 1,
    },
    modified: now.slice(0, paired).map((u, i) => ({ current: u, baseline: was[i] as LineRange })),
    added: now.slice(paired),
    removed: was.slice(paired),
    at: now.at(-1)?.end ?? span.cs,
  };
}

/** Whether marking blocks one by one would mark most of the document. */
export function mostlyRewritten(changes: BlockChange[], units: number, newFile: boolean): boolean {
  const marked = changes.reduce((sum, c) => sum + c.modified.length + c.added.length, 0);
  return newFile || (units > 0 && marked / units > 0.5);
}
