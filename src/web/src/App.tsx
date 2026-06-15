import type * as monaco from "monaco-editor";
import { For, type JSX, Show, createSignal, onCleanup, onMount } from "solid-js";
import { log, onHostMessage, postToHost } from "./bridge";
import { SAMPLE_CODE, createEditor } from "./editor/monaco-setup";
import { runBenchmark } from "./latency/benchmark";
import { LatencyMeter } from "./latency/latency-meter";
import { LoadGenerator } from "./latency/load-generator";
import type { BenchmarkReport, LatencySummary, LiveLatencyStats } from "./latency/types";

const BENCH_CONFIG = { keystrokes: 150, intervalMs: 50 };

const ms = (n: number): string => n.toFixed(1);

export default function App(): JSX.Element {
  let editorContainer!: HTMLDivElement;
  const [stats, setStats] = createSignal<LiveLatencyStats | null>(null);
  const [loadOn, setLoadOn] = createSignal(false);
  const [report, setReport] = createSignal<BenchmarkReport | null>(null);
  const [benchRunning, setBenchRunning] = createSignal(false);

  const meter = new LatencyMeter();
  const load = new LoadGenerator(5);
  let editor: monaco.editor.IStandaloneCodeEditor | undefined;

  const setLoad = (on: boolean): void => {
    if (on) {
      load.start();
    } else {
      load.stop();
    }
    meter.setLoadActive(on);
    setLoadOn(on);
  };

  const runBench = async (): Promise<void> => {
    if (editor === undefined || benchRunning()) {
      return;
    }
    setBenchRunning(true);
    const restoreLoad = loadOn();
    try {
      const result = await runBenchmark(editor, load, BENCH_CONFIG);
      setReport(result);
      postToHost({ type: "benchmark-result", report: result });
      log("info", `benchmark done: ${result.note}`);
      editor.getModel()?.setValue(SAMPLE_CODE);
    } catch (error) {
      log("error", `benchmark failed: ${String(error)}`);
    } finally {
      setLoad(restoreLoad);
      setBenchRunning(false);
    }
  };

  onMount(() => {
    editor = createEditor(editorContainer);
    meter.start();
    editor.focus();
    postToHost({ type: "monaco-ready" });

    const hudTimer = window.setInterval(() => {
      const snap = meter.snapshot();
      setStats(snap);
      postToHost({ type: "latency-live", stats: snap });
    }, 500);

    const offHost = onHostMessage((message) => {
      if (message.type === "set-load") {
        setLoad(message.enabled);
      } else if (message.type === "run-benchmark") {
        void runBench();
      }
    });

    // Auto-run once so the host captures objective numbers unattended; then the
    // editor is reset and free for manual feel-testing.
    const autoBench = window.setTimeout(() => {
      void runBench();
    }, 1500);

    onCleanup(() => {
      window.clearInterval(hudTimer);
      window.clearTimeout(autoBench);
      offHost();
      meter.dispose();
      load.stop();
      editor?.dispose();
    });
  });

  return (
    <div class="app">
      <Hud
        stats={stats()}
        loadOn={loadOn()}
        benchRunning={benchRunning()}
        report={report()}
        onToggleLoad={() => setLoad(!loadOn())}
        onRunBench={() => void runBench()}
      />
      <div class="editor" ref={editorContainer} />
    </div>
  );
}

function Hud(props: {
  stats: LiveLatencyStats | null;
  loadOn: boolean;
  benchRunning: boolean;
  report: BenchmarkReport | null;
  onToggleLoad: () => void;
  onRunBench: () => void;
}): JSX.Element {
  const hz = (): number => {
    const p50 = props.stats?.frameIntervalMs.p50 ?? 0;
    return p50 > 0 ? Math.round(1000 / p50) : 0;
  };

  return (
    <div class="hud">
      <div class="row">
        <span class="title">weavie · typing-latency gate</span>
        <Metric label="keydown→frame" summary={props.stats?.inputToFrame} />
        <Metric label="input→paint" summary={props.stats?.inputToPaint} brief />
        <span class="kv">
          handler p50 <b>{props.stats ? ms(props.stats.handler.p50) : "–"}</b>ms
        </span>
        <span class="kv">
          frame <b>{props.stats ? ms(props.stats.frameIntervalMs.p50) : "–"}</b>ms (<b>{hz()}</b>Hz)
        </span>
        <button type="button" classList={{ on: props.loadOn }} onClick={props.onToggleLoad}>
          load: {props.loadOn ? "ON" : "off"}
        </button>
        <button type="button" disabled={props.benchRunning} onClick={props.onRunBench}>
          {props.benchRunning ? "benchmarking…" : "run benchmark"}
        </button>
      </div>
      <Show when={props.report}>{(getReport) => <BenchTable report={getReport()} />}</Show>
    </div>
  );
}

function Metric(props: {
  label: string;
  summary: LatencySummary | undefined;
  brief?: boolean;
}): JSX.Element {
  const s = (): LatencySummary | undefined => props.summary;
  return (
    <span class="kv">
      {props.label} <b>{s() ? ms(s()!.p50) : "–"}</b>
      <span class="dim">/{s() ? ms(s()!.p95) : "–"}</span>
      <Show when={!props.brief}>
        <span class="dim">/{s() ? ms(s()!.p99) : "–"}</span>
        <span class="dim">/{s() ? ms(s()!.max) : "–"}</span>
      </Show>
      <span class="unit">ms{props.brief ? " p50/p95" : " p50/95/99/max"}</span>
    </span>
  );
}

function BenchTable(props: { report: BenchmarkReport }): JSX.Element {
  return (
    <div class="bench">
      <span class="benchnote">
        benchmark · {props.report.displayHz}Hz · {props.report.note}
      </span>
      <For each={props.report.phases}>
        {(phase) => (
          <span class="kv">
            <b>{phase.label}</b> edit→frame {ms(phase.editToFrame.p50)}/{ms(phase.editToFrame.p95)}/
            {ms(phase.editToFrame.p99)}/{ms(phase.editToFrame.max)} ms
            <span class="dim">
              {" "}
              (frame {ms(phase.frameIntervalMs.p50)}ms
              {phase.framesLookThrottled ? " ⚠throttled" : ""})
            </span>
          </span>
        )}
      </For>
    </div>
  );
}
