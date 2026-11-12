import type { ECharts } from "echarts/core";
import { createEffect, createSignal, type JSX, on, onCleanup, onMount, Show } from "solid-js";
import type { AgentBlockValue, AgentChart as AgentChartData } from "../bridge";
import { onPreviewThemeChanged } from "../theme/controller";
import type { BlockSelection } from "./agent-block-selection";
import { chartOption, chartPalette, chartRangeText, formatX, loadECharts } from "./agent-charts";

/** A block chart; dragging across it attaches that range, clicking a point attaches the point. */
export function AgentChart(props: {
  chart: AgentChartData;
  id: string;
  title: string;
  onSelect: (selection: BlockSelection) => void;
  onSelecting: (active: boolean) => void;
}): JSX.Element {
  let host: HTMLDivElement | undefined;
  let chart: ECharts | undefined;
  const [error, setError] = createSignal<string | null>(null);

  const draw = (): void => {
    if (chart === undefined) return;
    chart.setOption(chartOption(props.chart, chartPalette()), {
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
        chart = echarts.init(host, null, { renderer: "canvas" });
        draw();
        resize.observe(host);
        chart.getZr().on("mousedown", () => props.onSelecting(true));
        chart.getZr().on("mouseup", () => props.onSelecting(false));
        chart.on("brushEnd", (event) => {
          const range = (event as { areas: { coordRange?: AgentBlockValue[] }[] }).areas[0]
            ?.coordRange;
          chart?.dispatchAction({ type: "brush", areas: [] });
          if (range === undefined) return;
          const [from, to] = range as [AgentBlockValue, AgentBlockValue];
          props.onSelect({
            label: `${props.title} · ${formatX(props.chart, from)} to ${formatX(props.chart, to)}`,
            text: chartRangeText(props.title, props.id, props.chart, from, to),
          });
        });
        chart.on("click", (point) => {
          if (point.seriesName === undefined || !Array.isArray(point.value)) return;
          const [x, y] = point.value as [AgentBlockValue, AgentBlockValue];
          props.onSelect({
            label: `${props.title} · ${point.seriesName} at ${formatX(props.chart, x)}`,
            text: `Chart "${props.title}" (${props.id}), series "${point.seriesName}" at ${formatX(props.chart, x)}: ${y ?? ""}`,
          });
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
