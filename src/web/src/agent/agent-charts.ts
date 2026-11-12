import type { EChartsCoreOption } from "echarts/core";
import type { AgentChart, AgentChartSeries } from "../bridge";

type ECharts = typeof import("echarts/core");
let echartsPromise: Promise<ECharts> | undefined;

/** ECharts is large, so only a pane that shows a chart downloads it; the import happens once. */
export function loadECharts(): Promise<ECharts> {
  echartsPromise ??= Promise.all([
    import("echarts/core"),
    import("echarts/charts"),
    import("echarts/components"),
    import("echarts/renderers"),
  ]).then(([core, charts, components, renderers]) => {
    core.use([
      charts.LineChart,
      charts.BarChart,
      charts.ScatterChart,
      components.GridComponent,
      components.TooltipComponent,
      components.LegendComponent,
      components.MarkLineComponent,
      components.MarkAreaComponent,
      components.BrushComponent,
      renderers.SVGRenderer,
      renderers.CanvasRenderer,
    ]);
    return core;
  });
  return echartsPromise;
}

const svgMarkLimit = 2000;

/**
 * SVG draws text as the page does, so it is the renderer until a chart has too many marks for the DOM: a bar or a
 * scatter point is an element each, while a line is one path however long.
 */
export function chartRenderer(chart: AgentChart): "svg" | "canvas" {
  const marks =
    chart.kind === "bar" || chart.kind === "scatter"
      ? chart.series.reduce((sum, line) => sum + line.points.length, 0)
      : 0;
  return marks > svgMarkLimit ? "canvas" : "svg";
}

/** The theme a chart draws with, read from Weavie's live CSS variables. */
export interface ChartPalette {
  fg: string;
  dim: string;
  border: string;
  bar: string;
  accent: string;
  bad: string;
  series: string[];
  font: string;
  size: number;
}

/** The palette from the theme's variables; type follows `host`'s chrome role, set in CSS. */
export function chartPalette(host: HTMLElement): ChartPalette {
  const css = getComputedStyle(document.documentElement);
  const type = getComputedStyle(host);
  const v = (name: string): string => css.getPropertyValue(name).trim();
  return {
    fg: v("--fg"),
    dim: v("--dim"),
    border: v("--border"),
    bar: v("--bar"),
    accent: v("--accent"),
    bad: v("--bad"),
    series: [1, 2, 3, 4, 5, 6].map((index) => v(`--chart-${index}`)),
    font: type.fontFamily,
    size: Number.parseFloat(type.fontSize),
  };
}

/** The ECharts option for `chart`: no entrance animation, animated updates, Weavie's theme. */
export function chartOption(chart: AgentChart, palette: ChartPalette): EChartsCoreOption {
  const axisLabel = { color: palette.dim, fontFamily: palette.font, fontSize: palette.size };
  const yAxis = (axis: AgentChart["y"], grid: boolean) => ({
    type: "value",
    name: axis?.label,
    nameLocation: "middle",
    nameTextStyle: axisLabel,
    axisLabel: { ...axisLabel, formatter: (value: number) => `${value}${axis?.unit ?? ""}` },
    splitLine: { show: grid, lineStyle: { color: palette.border } },
  });
  let colored = 0;
  const series: object[] = chart.series.map((line: AgentChartSeries) => {
    const comparison = line.style === "comparison";
    const color = comparison ? palette.dim : palette.series[colored++ % palette.series.length];
    return {
      name: line.name,
      type: chart.kind === "area" ? "line" : chart.kind,
      data: line.points,
      yAxisIndex: line.axis === "y2" ? 1 : 0,
      color,
      showSymbol: chart.kind === "scatter",
      symbolSize: 6,
      lineStyle: { width: comparison ? 1.25 : 1.75, type: comparison ? [4, 3] : "solid" },
      areaStyle: chart.kind === "area" && !comparison ? { opacity: 0.12 } : undefined,
    };
  });
  // Thresholds and bands ride an empty series per y axis, so they never depend on which series exist.
  for (const axis of chart.y2 == null ? ["y"] : ["y", "y2"]) {
    const onAxis = (mark: { axis?: string | null }) => (mark.axis ?? "y") === axis;
    series.push({
      type: "line",
      data: [],
      yAxisIndex: axis === "y2" ? 1 : 0,
      silent: true,
      tooltip: { show: false },
      markLine: {
        silent: true,
        symbol: "none",
        lineStyle: { color: palette.bad, type: [2, 3] },
        label: {
          color: palette.bad,
          fontFamily: palette.font,
          fontSize: palette.size,
          position: "insideStartTop",
        },
        data: (chart.thresholds ?? [])
          .filter(onAxis)
          .map((mark) => ({ yAxis: mark.value, name: mark.label ?? "" })),
      },
      markArea: {
        silent: true,
        itemStyle: { color: palette.accent, opacity: 0.1 },
        label: { color: palette.dim, fontFamily: palette.font, fontSize: palette.size },
        data: (chart.bands ?? [])
          .filter((band) => (axis === "y" && (band.axis ?? "x") === "x") || band.axis === axis)
          .map((band) => {
            const key = (band.axis ?? "x") === "x" ? "xAxis" : "yAxis";
            return [{ [key]: band.from, name: band.label ?? "" }, { [key]: band.to }];
          }),
      },
    });
  }
  return {
    animationDuration: 0,
    animationDurationUpdate: 300,
    textStyle: { fontFamily: palette.font, color: palette.dim },
    // The grid shrinks inside these bounds to fit its axis labels and names, so neither meets the legend.
    grid: {
      left: 8,
      right: 8,
      top: chart.series.length > 1 ? 36 : 16,
      bottom: 4,
      outerBoundsMode: "same",
      outerBoundsContain: "all",
    },
    legend: {
      show: chart.series.length > 1,
      type: "scroll",
      data: chart.series.map((line) => line.name),
      left: 0,
      top: 0,
      icon: "roundRect",
      itemWidth: 14,
      itemHeight: 2,
      textStyle: axisLabel,
    },
    tooltip: {
      trigger: chart.kind === "scatter" ? "item" : "axis",
      backgroundColor: palette.bar,
      borderColor: palette.border,
      textStyle: { color: palette.fg, fontFamily: palette.font, fontSize: palette.size },
      extraCssText: "box-shadow: none",
    },
    xAxis: {
      type: chart.x.type === "number" ? "value" : chart.x.type,
      name: chart.x.label,
      nameLocation: "middle",
      nameTextStyle: axisLabel,
      axisLabel: { ...axisLabel, hideOverlap: true },
      axisLine: { lineStyle: { color: palette.border } },
      axisTick: { show: false },
      splitLine: { show: false },
    },
    yAxis:
      chart.y2 == null ? [yAxis(chart.y, true)] : [yAxis(chart.y, true), yAxis(chart.y2, false)],
    brush: {
      xAxisIndex: 0,
      brushType: "lineX",
      brushMode: "single",
      brushStyle: { color: palette.accent, opacity: 0.16, borderWidth: 0 },
      throttleType: "debounce",
      removeOnClick: true,
    },
    series,
  };
}
