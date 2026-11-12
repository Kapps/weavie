import type { ECharts } from "echarts/core";
import { createEffect, createSignal, type JSX, on, onCleanup, onMount, Show } from "solid-js";
import type { AgentBlockValue, AgentChart as AgentChartData } from "../bridge";
import { onPreviewThemeChanged } from "../theme/controller";
import { chartPointSelection, chartRangeSelection } from "./agent-block-selection";
import { chartOption, chartPalette, loadECharts } from "./agent-charts";
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
  const [error, setError] = createSignal<string | null>(null);
  const block = () => ({ id: props.id, title: props.title });

  const draw = (): void => {
    if (chart === undefined || host === undefined) return;
    chart.setOption(chartOption(props.chart, chartPalette(host)), {
      replaceMerge: ["series", "yAxis"],
    });
    chart.dispatchAction({
      type: "takeGlobalCursor",
      key: "brush",
      brushOption: { brushType: "lineX", brushMode: "single" },
    });
  };

  onMount(() => {
    let disposed = false;
    const resize = new ResizeObserver(() => chart?.resize());
    loadECharts()
      .then((echarts) => {
        if (disposed || host === undefined) return;
        // Canvas: WebKit can leave an SVG chart's lines unpainted while its elements are all in place.
        const mounted = echarts.init(host, null, { renderer: "canvas" });
        chart = mounted;
        draw();
        resize.observe(host);
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
