import type { ECharts } from "echarts/core";
import { createEffect, createSignal, type JSX, on, onCleanup, onMount, Show } from "solid-js";
import type { AgentBlockValue, AgentChart as AgentChartData } from "../bridge";
import { onPreviewThemeChanged } from "../theme/controller";
import { chartPointSelection, chartRangeSelection } from "./agent-block-selection";
import { chartOption, chartPalette, chartRenderer, loadECharts } from "./agent-charts";
import type { AgentSelection } from "./composer-state";

/** A block chart; dragging across it attaches that range, clicking a point attaches the point. */
export function AgentChart(props: {
  chart: AgentChartData;
  id: string;
  title: string;
  onSelect: (selection: AgentSelection) => void;
  onSelecting: (active: boolean) => void;
}): JSX.Element {
  let host: HTMLDivElement | undefined;
  let chart: ECharts | undefined;
  let echarts: Awaited<ReturnType<typeof loadECharts>> | undefined;
  let renderer: ReturnType<typeof chartRenderer> | undefined;
  const [error, setError] = createSignal<string | null>(null);
  const block = () => ({ id: props.id, title: props.title });

  const draw = (): void => {
    if (echarts === undefined || host === undefined) return;
    if (renderer !== chartRenderer(props.chart)) mount(echarts, host);
    if (chart === undefined) return;
    chart.setOption(chartOption(props.chart, chartPalette(host)), {
      replaceMerge: ["series", "yAxis"],
    });
    chart.dispatchAction({
      type: "takeGlobalCursor",
      key: "brush",
      brushOption: { brushType: "lineX", brushMode: "single" },
    });
  };

  // A chart is one renderer for life, so data that crosses to the other renderer starts a new chart.
  const mount = (core: NonNullable<typeof echarts>, element: HTMLDivElement): void => {
    chart?.dispose();
    renderer = chartRenderer(props.chart);
    const mounted = core.init(element, null, { renderer });
    chart = mounted;
    mounted.getZr().on("mousedown", () => props.onSelecting(true));
    mounted.getZr().on("mouseup", () => props.onSelecting(false));
    mounted.on("brushEnd", (event) => {
      const range = (event as { areas: { coordRange?: number[] }[] }).areas[0]?.coordRange;
      mounted.dispatchAction({ type: "brush", areas: [] });
      if (range?.length === 2)
        props.onSelect(chartRangeSelection(block(), props.chart, range[0]!, range[1]!));
    });
    mounted.on("click", (point) => {
      if (point.seriesName === undefined || !Array.isArray(point.value)) return;
      props.onSelect(
        chartPointSelection(
          block(),
          props.chart,
          point.seriesName,
          point.value as [AgentBlockValue, AgentBlockValue],
        ),
      );
    });
  };

  onMount(() => {
    let disposed = false;
    const resize = new ResizeObserver(() => chart?.resize());
    loadECharts()
      .then((core) => {
        if (disposed || host === undefined) return;
        echarts = core;
        draw();
        resize.observe(host);
      })
      .catch((failure: unknown) => setError(`Chart unavailable: ${String(failure)}`));
    const offTheme = onPreviewThemeChanged(draw);
    onCleanup(() => {
      disposed = true;
      offTheme();
      resize.disconnect();
      chart?.dispose();
    });
  });
  createEffect(on(() => props.chart, draw, { defer: true }));

  return (
    <Show
      when={error() === null}
      fallback={
        <p class="agent-block-error" role="status">
          {error()}
        </p>
      }
    >
      <div class="agent-chart" ref={host} />
    </Show>
  );
}
