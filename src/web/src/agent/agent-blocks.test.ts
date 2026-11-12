import { describe, expect, it } from "vitest";
import type { AgentChart } from "../bridge";
import { estimateEntrySize } from "./AgentPaneEstimate";
import { blockKey } from "./AgentPaneIdentity";
import { toAgentTranscript } from "./AgentPaneTestModel";
import { blockHeight } from "./agent-block-layout";
import { chartRangeSelection, wholeBlockSelection } from "./agent-block-selection";
import { chartOption } from "./agent-charts";
import { sourceRect } from "./agent-crop";

const chart: AgentChart = {
  kind: "area",
  x: { type: "time", label: "time" },
  y: { unit: "ms" },
  y2: { unit: "%" },
  series: [
    {
      name: "now",
      points: [
        [1000, 1],
        [2000, 2],
        [3000, 3],
      ],
    },
    {
      name: "24h ago",
      style: "comparison",
      points: [
        [1000, 4],
        [3000, 6],
      ],
    },
    { name: "errors", axis: "y2", points: [[2000, 0.5]] },
  ],
  thresholds: [{ label: "SLO", value: 2.5 }],
  bands: [{ label: "deploy", from: 1500, to: 2500 }],
};

const palette = {
  fg: "fg",
  dim: "dim",
  border: "border",
  bar: "bar",
  accent: "accent",
  bad: "bad",
  series: ["c1", "c2"],
  font: "font",
  size: 13,
};

describe("agent blocks", () => {
  it("draws a chart with no entrance animation, dim comparisons, and marks on their own axes", () => {
    const option = chartOption(chart, palette) as {
      animationDuration: number;
      animationDurationUpdate: number;
      series: {
        name?: string;
        color?: string;
        yAxisIndex: number;
        lineStyle?: { type: unknown };
        markLine?: { data: unknown[] };
        markArea?: { data: unknown[] };
      }[];
      yAxis: unknown[];
    };

    expect([option.animationDuration, option.animationDurationUpdate]).toEqual([0, 300]);
    expect(
      option.series.slice(0, 3).map((series) => [series.name, series.color, series.yAxisIndex]),
    ).toEqual([
      ["now", "c1", 0],
      ["24h ago", "dim", 0],
      ["errors", "c2", 1],
    ]);
    expect(option.series[1]!.lineStyle!.type).toEqual([4, 3]);
    expect(option.yAxis).toHaveLength(2);
    expect(option.series[3]!.markLine!.data).toEqual([{ yAxis: 2.5, name: "SLO" }]);
    expect(option.series[3]!.markArea!.data).toHaveLength(1);
    expect(option.series[4]!.markLine!.data).toEqual([]);
  });

  it("draws one y axis when the wire sends y2 as null", () => {
    const option = chartOption(
      { ...chart, y2: null, series: chart.series.slice(0, 2) },
      palette,
    ) as {
      yAxis: unknown[];
      series: unknown[];
    };
    expect(option.yAxis).toHaveLength(1);
    expect(option.series).toHaveLength(3);
  });

  it("describes a selected range as the points inside it", () => {
    const selection = chartRangeSelection({ id: "p95", title: "p95" }, chart, 1500, 3000);
    expect(selection.text.split("\n")).toEqual([
      'Chart "p95" (p95), 1970-01-01T00:00:01.500Z to 1970-01-01T00:00:03.000Z',
      "time,now,24h ago,errors",
      "1970-01-01T00:00:02.000Z,2,,0.5",
      "1970-01-01T00:00:03.000Z,3,6,",
    ]);
    expect(selection.label).not.toContain("1970-01-01T");
  });

  it("maps a category brush's indices back to its categories", () => {
    const builds: AgentChart = {
      kind: "bar",
      x: { type: "category", label: "build" },
      series: [
        {
          name: "errors",
          points: [
            ["2691", 13],
            ["2692", 16],
            ["2693", 47],
          ],
        },
      ],
    };
    const selection = chartRangeSelection({ id: "e", title: "Errors" }, builds, 0.6, 2);
    expect(selection.label).toBe("Errors · 2692 to 2693");
    expect(selection.text.split("\n").slice(1)).toEqual(["build,errors", "2692,16", "2693,47"]);
  });

  it("projects a block row with a fixed estimate", () => {
    const [entry] = toAgentTranscript([
      {
        type: "item-completed",
        providerId: "fake",
        threadId: "t",
        turnId: "1",
        itemId: "tool:1",
        itemType: "block",
        category: "table",
        summary: "Errors",
        blockId: "errors",
      },
    ]);

    expect(entry).toMatchObject({
      kind: "block",
      label: "Table",
      summary: "Errors",
      block: { key: blockKey("fake", null, "errors"), kind: "table" },
    });
    expect(estimateEntrySize(entry)).toBe(blockHeight("table"));
  });
});

describe("sourceRect", () => {
  const frame = { width: 400, height: 200 };
  const natural = { width: 100, height: 100 };

  it("maps a box over a left-aligned letterboxed picture to its own pixels", () => {
    expect(
      sourceRect(frame, natural, { x: 0, y: 0.5 }, { x: 0, y: 0, width: 100, height: 100 }),
    ).toEqual({
      x: 0,
      y: 0,
      width: 50,
      height: 50,
    });
    expect(
      sourceRect(frame, natural, { x: 0.5, y: 0.5 }, { x: 100, y: 0, width: 100, height: 100 }),
    ).toEqual({
      x: 0,
      y: 0,
      width: 50,
      height: 50,
    });
  });

  it("refuses a box over the empty part of the frame", () => {
    expect(
      sourceRect(frame, natural, { x: 0, y: 0.5 }, { x: 300, y: 0, width: 80, height: 80 }),
    ).toBeNull();
  });
});

describe("block selections", () => {
  it("quotes CSV values and attaches every row of a whole table", () => {
    const selection = wholeBlockSelection({
      id: "errors",
      title: "Errors",
      table: {
        columns: [
          { name: "time", type: "time" },
          { name: "message", type: "text" },
        ],
        rows: [[0, 'timed out, "upstream"']],
      },
    });
    expect(selection.label).toBe("Errors · all 1 rows");
    expect(selection.text.split("\n")).toEqual([
      'Table "Errors" (errors), all 1 rows',
      "time,message",
      '1970-01-01T00:00:00.000Z,"timed out, ""upstream"""',
    ]);
  });
});

describe("whole-block selections", () => {
  it("attaches an empty time chart, and skips points without an x", () => {
    const empty = wholeBlockSelection({
      id: "e",
      title: "Empty",
      chart: { kind: "line", x: { type: "time" }, series: [{ name: "now", points: [] }] },
    });
    expect(empty.label).toBe("Empty · all 0 points");
    expect(empty.text.split("\n")).toEqual(['Chart "Empty" (e), all 0 points', "x,now"]);

    const gappy = wholeBlockSelection({
      id: "g",
      title: "Gappy",
      chart: {
        kind: "line",
        x: { type: "number" },
        series: [
          {
            name: "s",
            points: [
              [2, 20],
              [null, 5],
              [1, 10],
            ],
          },
        ],
      },
    });
    expect(gappy.text.split("\n").slice(1)).toEqual(["x,s", "1,10", "2,20"]);
  });
});
