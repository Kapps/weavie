import { For, type JSX, Show, createSignal, onCleanup, onMount } from "solid-js";
import { type TermSession, log, onHostMessage, postToHost } from "./bridge";
import { type ActiveDiff, DiffView } from "./diff/DiffView";
import { SAMPLE_CODE, createEditor, monaco } from "./editor/monaco-setup";
import { runBenchmark } from "./latency/benchmark";
import { runFpsProbe } from "./latency/fps-probe";
import { LatencyMeter } from "./latency/latency-meter";
import { LoadGenerator } from "./latency/load-generator";
import type { BenchmarkReport, LatencySummary, LiveLatencyStats } from "./latency/types";
import { startLanguageServices } from "./lsp/lsp-client";
import { TerminalView } from "./terminal/TerminalView";
import { DEFAULT_DARK_PALETTE, applyColorsToCssVars, resolveColors } from "./theme";

const BENCH_CONFIG = { keystrokes: 150, intervalMs: 50 };

// The performance debug surface — the latency HUD bar, the live meter (a permanent rAF loop +
// Event-Timing observer), the fps probe, the auto-bench, and the twice-a-second latency-live
// message to the host — is gated behind ?debugperf, which the host sets from WEAVIE_DEBUG_PERFORMANCE.
// Off by default: normal use gets the clean two-pane UI with no instrumentation overhead and no
// host log spam. (A future in-app setting will flip this at runtime; for now it's launch-only.)
const DEBUG_PERF = new URLSearchParams(location.search).has("debugperf");

const ms = (n: number): string => n.toFixed(1);

export default function App(): JSX.Element {
  let editorContainer!: HTMLDivElement;
  let splitContainer!: HTMLDivElement;
  // Width of the left (terminal) column as a % of the split; the editor takes the rest.
  const [leftPct, setLeftPct] = createSignal(40);
  // Which left-column pane is "active" and expands to 80% of the column height. Driven by focus.
  const [activeLeft, setActiveLeft] = createSignal<TermSession>("claude");
  const [stats, setStats] = createSignal<LiveLatencyStats | null>(null);
  const [loadOn, setLoadOn] = createSignal(false);
  const [report, setReport] = createSignal<BenchmarkReport | null>(null);
  const [benchRunning, setBenchRunning] = createSignal(false);
  const [activeDiff, setActiveDiff] = createSignal<ActiveDiff | null>(null);
  // Device-pixel ratio: 1 == native 1x (text rendered one device pixel per CSS pixel),
  // 2 == HiDPI/Retina. Drives how "antialiased" the editor text looks. Polled in the HUD
  // tick so dragging the window to a differently-scaled monitor updates it.
  const [dpr, setDpr] = createSignal(window.devicePixelRatio);

  const startDrag = (event: PointerEvent): void => {
    event.preventDefault();
    const onMove = (move: PointerEvent): void => {
      const rect = splitContainer.getBoundingClientRect();
      const pct = ((move.clientX - rect.left) / rect.width) * 100;
      setLeftPct(Math.min(80, Math.max(20, pct)));
    };
    const onUp = (): void => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
    };
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
  };

  const resolveDiff = (kept: boolean, finalContents: string): void => {
    const diff = activeDiff();
    if (diff === null) {
      return;
    }
    postToHost({ type: "diff-resolved", id: diff.id, kept, finalContents });
    setActiveDiff(null);
  };

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

  const openFileInEditor = (path: string, content: string, line: number): void => {
    if (editor === undefined) {
      return;
    }
    const uri = monaco.Uri.file(path);
    const existing = monaco.editor.getModel(uri);
    const model = existing ?? monaco.editor.createModel(content, undefined, uri);
    if (existing) {
      existing.setValue(content);
    }
    editor.setModel(model);
    editor.revealLineInCenter(line);
    editor.setPosition({ lineNumber: line, column: 1 });
    editor.focus();
  };

  onMount(() => {
    // Theme chrome from the default palette (spec §6 application surface). Override ops layer here once
    // wired to settings/MCP; for now this publishes --weavie-* CSS vars for the chrome to consume.
    applyColorsToCssVars(resolveColors(DEFAULT_DARK_PALETTE, []));

    // Editor init must not abort the shared onMount queue: a throw here would otherwise prevent the
    // sibling terminal panes from mounting (and emitting term-ready), blanking the whole left column.
    try {
      editor = createEditor(editorContainer);
      editor.focus();
      postToHost({ type: "monaco-ready" });
      // Lazy per-language LSP via the bridge (no-op if the host didn't inject bridge config); a client
      // connects the first time a document of its language is open.
      startLanguageServices();
    } catch (error) {
      log("error", `editor init failed: ${String(error)}`);
    }

    // All live perf instrumentation is opt-in via ?debugperf. When off we never start the meter,
    // the HUD tick, the fps probe, or the auto-bench — so the shipped UI carries none of their cost
    // and posts no latency-live spam. The fpsprobe/autobench sub-flags only take effect under it.
    let hudTimer = 0;
    let autoBench = 0;
    if (DEBUG_PERF) {
      meter.start();

      if (new URLSearchParams(location.search).has("fpsprobe")) {
        runFpsProbe();
      }

      hudTimer = window.setInterval(() => {
        const snap = meter.snapshot();
        setStats(snap);
        setDpr(window.devicePixelRatio);
        postToHost({ type: "latency-live", stats: snap });
      }, 500);

      // Auto-run once (only when the host requests it via ?autobench=1) so unattended captures get
      // objective numbers; the editor then resets for manual feel-testing. In normal use the user
      // clicks "run benchmark" instead.
      autoBench = new URLSearchParams(location.search).has("autobench")
        ? window.setTimeout(() => {
            void runBench();
          }, 1500)
        : 0;
    }

    const offHost = onHostMessage((message) => {
      if (message.type === "set-load") {
        setLoad(message.enabled);
      } else if (message.type === "run-benchmark") {
        void runBench();
      } else if (message.type === "show-diff") {
        setActiveDiff({
          id: message.id,
          path: message.path,
          tabName: message.tabName,
          original: message.original,
          proposed: message.proposed,
        });
      } else if (message.type === "close-diff") {
        if (activeDiff()?.id === message.id) {
          setActiveDiff(null);
        }
      } else if (message.type === "open-file") {
        openFileInEditor(message.path, message.content, message.line);
      }
    });

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
      <Show when={DEBUG_PERF}>
        <Hud
          stats={stats()}
          dpr={dpr()}
          loadOn={loadOn()}
          benchRunning={benchRunning()}
          report={report()}
          onToggleLoad={() => setLoad(!loadOn())}
          onRunBench={() => void runBench()}
        />
      </Show>
      <div class="split" ref={splitContainer}>
        <div class="left-col" style={`flex: 0 0 ${leftPct()}%`}>
          <TerminalPane
            session="claude"
            label="Claude Code"
            active={activeLeft() === "claude"}
            onActivate={() => setActiveLeft("claude")}
          />
          <TerminalPane
            session="shell"
            label="Terminal"
            active={activeLeft() === "shell"}
            onActivate={() => setActiveLeft("shell")}
          />
        </div>
        <div class="splitter" onPointerDown={startDrag} />
        <div class="pane editor-pane">
          <div class="editor" ref={editorContainer} />
          <Show when={activeDiff()}>
            {(diff) => <DiffView diff={diff()} onResolve={resolveDiff} />}
          </Show>
        </div>
      </div>
    </div>
  );
}

// One pane in the left column: a titled, focus-driven accordion section wrapping a terminal
// session. Selecting it (click or keyboard) makes it "active", expanding it to 80% of the column.
//
// We must resize only *after* the pointer is released, never on pointerdown/focusin: a press inside
// an inactive pane focuses xterm (firing focusin), and resizing then would grow the pane while the
// button is held, sliding the text up under the stationary cursor — which the browser reports as
// mousemove and xterm turns into a stray drag-selection. So pointer activation waits for the click,
// and the focusin path is gated to keyboard focus (no pointer down) only.
function TerminalPane(props: {
  session: TermSession;
  label: string;
  active: boolean;
  onActivate: () => void;
}): JSX.Element {
  let pointerDown = false;
  return (
    <div
      class="left-pane"
      classList={{ active: props.active }}
      onPointerDown={() => {
        pointerDown = true;
      }}
      onPointerUp={() => {
        pointerDown = false;
        props.onActivate();
      }}
      onPointerLeave={() => {
        pointerDown = false;
      }}
      onFocusIn={() => {
        if (!pointerDown) {
          props.onActivate();
        }
      }}
    >
      <div class="pane-head">
        <span class="pane-label">{props.label}</span>
      </div>
      <div class="pane-body">
        <TerminalView session={props.session} />
      </div>
    </div>
  );
}

function Hud(props: {
  stats: LiveLatencyStats | null;
  dpr: number;
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
        <span class="kv">
          dpr <b>{props.dpr.toFixed(2)}</b>
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
