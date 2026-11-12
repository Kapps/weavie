import { describe, expect, it } from "vitest";
import type { AgentChart } from "../bridge";
import { estimateEntrySize } from "./AgentPaneEstimate";
import { blockKey } from "./AgentPaneIdentity";
import { toAgentTranscript } from "./AgentPaneTestModel";
import { blockHeight } from "./agent-block-layout";
import { chartOption, chartRangeText } from "./agent-charts";

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

  it("describes a selected range as the points inside it", () => {
    expect(chartRangeText("p95", "p95", chart, 1500, 3000).split("\n")).toEqual([
      'Chart "p95" (p95), 1970-01-01T00:00:01.500Z to 1970-01-01T00:00:03.000Z',
      "time,now,24h ago,errors",
      "1970-01-01T00:00:02.000Z,2,,0.5",
      "1970-01-01T00:00:03.000Z,3,6,",
    ]);
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
