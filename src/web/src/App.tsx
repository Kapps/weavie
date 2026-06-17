import {
  For,
  type JSX,
  Show,
  Suspense,
  createEffect,
  createMemo,
  createSignal,
  lazy,
  onCleanup,
  onMount,
} from "solid-js";
import { type TermSession, log, onHostMessage, postToHost } from "./bridge";
import type { ActiveDiff } from "./diff/DiffView";
import type { EditorHost } from "./editor/editor-host";
import { runFpsProbe } from "./latency/fps-probe";
import { LatencyMeter } from "./latency/latency-meter";
import { LoadGenerator } from "./latency/load-generator";
import type { BenchmarkReport, LatencySummary, LiveLatencyStats } from "./latency/types";
import { LayoutView } from "./layout/LayoutView";
import { paneOrder } from "./layout/geometry";
import { layoutDocument, sendLayout } from "./layout/store";
import type { LayoutNode } from "./layout/types";
import { dismissSplash } from "./splash";
import { mark } from "./startup-timing";
import { TerminalView } from "./terminal/TerminalView";
import { DEFAULT_DARK_PALETTE, applyColorsToCssVars, resolveColors } from "./theme";

// The Monaco editor and its VSCode service layer are the heaviest code in the app. They live behind a
// dynamic import (see onMount → editor-host) so the entry chunk — and thus first paint — carries none
// of it. DiffView also pulls Monaco, so it's lazy too; by the time a diff arrives the chunk is warm.
const DiffView = lazy(() => import("./diff/DiffView").then((m) => ({ default: m.DiffView })));

const BENCH_CONFIG = { keystrokes: 150, intervalMs: 50 };

// The performance debug surface — the latency HUD bar, the live meter (a permanent rAF loop +
// Event-Timing observer), the fps probe, the auto-bench, and the twice-a-second latency-live
// message to the host — is gated behind ?debugperf, which the host sets from WEAVIE_DEBUG_PERFORMANCE.
// Off by default: normal use gets the clean two-pane UI with no instrumentation overhead and no
// host log spam. (A future in-app setting will flip this at runtime; for now it's launch-only.)
const DEBUG_PERF = new URLSearchParams(location.search).has("debugperf");

const ms = (n: number): string => n.toFixed(1);

// Modifier label for the pane-switch shortcut badge: the ⌃ glyph on macOS, "Ctrl+" elsewhere.
const CTRL_LABEL = /Mac/i.test(navigator.userAgent) ? "⌃" : "Ctrl+";

// The default layout (mirrors Weavie.Core.Layout's seeded default): a left column stacking the Claude
// and shell terminals beside the editor, 40/60. Shown until the host pushes the persisted layout.
const DEFAULT_ROOT: LayoutNode = {
  type: "split",
  dir: "row",
  weights: [0.4, 0.6],
  children: [
    {
      type: "split",
      dir: "column",
      weights: [0.5, 0.5],
      children: [
        { type: "pane", id: "p_claude", kind: "terminal:claude" },
        { type: "pane", id: "p_shell", kind: "terminal:shell" },
      ],
    },
    { type: "pane", id: "p_editor", kind: "editor" },
  ],
};

export default function App(): JSX.Element {
  let editorContainer!: HTMLDivElement;
  // The live pane layout tree: seeded with the default, replaced when the host pushes the persisted
  // layout, and updated optimistically while the user drags a splitter.
  const [layoutRoot, setLayoutRoot] = createSignal<LayoutNode>(DEFAULT_ROOT);
  // The pane that currently has keyboard focus (tracked from focusin), for the active highlight.
  const [focusedKind, setFocusedKind] = createSignal<string | null>(null);
  // Pane kinds in DFS order; index + 1 is the pane's Ctrl+N number.
  const paneNumbers = createMemo(() => paneOrder(layoutRoot()));
  const numberOf = (kind: string): number => paneNumbers().indexOf(kind) + 1;
  // A terminal registers its focus fn here on mount (the editor focuses via its host directly).
  const terminalFocus = new Map<string, () => void>();
  const focusPane = (kind: string): void => {
    if (kind === "editor") {
      host?.editor.focus();
      return;
    }
    terminalFocus.get(kind)?.();
  };
  const [stats, setStats] = createSignal<LiveLatencyStats | null>(null);
  const [loadOn, setLoadOn] = createSignal(false);
  const [report, setReport] = createSignal<BenchmarkReport | null>(null);
  const [benchRunning, setBenchRunning] = createSignal(false);
  const [activeDiff, setActiveDiff] = createSignal<ActiveDiff | null>(null);
  // Device-pixel ratio: 1 == native 1x (text rendered one device pixel per CSS pixel),
  // 2 == HiDPI/Retina. Drives how "antialiased" the editor text looks. Polled in the HUD
  // tick so dragging the window to a differently-scaled monitor updates it.
  const [dpr, setDpr] = createSignal(window.devicePixelRatio);

  // Persist the layout after a user gesture (debounced). Skipped until the host has pushed the initial
  // layout, so we never overwrite the saved state with the default before it has loaded.
  let persistTimer = 0;
  const persistRoot = (root: LayoutNode): void => {
    const base = layoutDocument();
    if (base === null) {
      return;
    }
    window.clearTimeout(persistTimer);
    persistTimer = window.setTimeout(() => {
      sendLayout({ ...base, root });
    }, 400);
  };

  // A splitter drag: show the new sizes immediately, persist on a debounce.
  const onLayoutResize = (root: LayoutNode): void => {
    setLayoutRoot(root);
    persistRoot(root);
  };

  // Apply the layout the host pushes (startup restore + any later host/MCP change). The resize handler
  // is gesture-driven, so applying a pushed layout never echoes back into a save.
  createEffect(() => {
    const doc = layoutDocument();
    if (doc !== null) {
      setLayoutRoot(doc.root);
    }
  });

  // Renders the surface for a pane kind. Called once per kind by LayoutView (the slot list is stable),
  // so the editor and terminals are created a single time and only repositioned thereafter.
  const renderPane = (kind: string): JSX.Element => {
    if (kind === "editor") {
      return (
        <div
          class="editor-surface"
          classList={{ active: focusedKind() === "editor" }}
          data-kind="editor"
        >
          <div class="editor" ref={editorContainer} />
          <span class="pane-shortcut editor-badge">
            {CTRL_LABEL}
            {numberOf("editor")}
          </span>
          <Show when={activeDiff()}>
            {(diff) => (
              <Suspense>
                <DiffView diff={diff()} onResolve={resolveDiff} />
              </Suspense>
            )}
          </Show>
        </div>
      );
    }
    const session: TermSession = kind === "terminal:claude" ? "claude" : "shell";
    return (
      <div class="terminal-surface" classList={{ active: focusedKind() === kind }} data-kind={kind}>
        <div class="pane-head">
          <span class="pane-label">{kind === "terminal:claude" ? "Claude Code" : "Terminal"}</span>
          <span class="pane-shortcut">
            {CTRL_LABEL}
            {numberOf(kind)}
          </span>
        </div>
        <div class="pane-body">
          <TerminalView session={session} onReady={(focus) => terminalFocus.set(kind, focus)} />
        </div>
      </div>
    );
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
  // The Monaco editor host, set once its chunk has loaded and the editor is created (see onMount).
  let host: EditorHost | undefined;
  // An open-file request that arrived before the editor was ready; replayed when it is (last wins).
  let pendingOpen: { path: string; content: string; line: number } | undefined;

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
    if (host === undefined || benchRunning()) {
      return;
    }
    setBenchRunning(true);
    const restoreLoad = loadOn();
    try {
      // benchmark.ts pulls Monaco, so it's loaded on demand (debugperf-only) rather than at startup.
      const { runBenchmark } = await import("./latency/benchmark");
      const result = await runBenchmark(host.editor, load, BENCH_CONFIG);
      setReport(result);
      postToHost({ type: "benchmark-result", report: result });
      log("info", `benchmark done: ${result.note}`);
      host.resetSample();
    } catch (error) {
      log("error", `benchmark failed: ${String(error)}`);
    } finally {
      setLoad(restoreLoad);
      setBenchRunning(false);
    }
  };

  const openFileInEditor = (path: string, content: string, line: number): void => {
    if (host !== undefined) {
      host.openFile(path, content, line);
    } else {
      // Editor chunk not loaded yet — remember the request and replay it once the host is ready.
      pendingOpen = { path, content, line };
    }
  };

  onMount(() => {
    // Theme chrome from the default palette (spec §6 application surface). Override ops layer here once
    // wired to settings/MCP; for now this publishes --weavie-* CSS vars for the chrome to consume.
    applyColorsToCssVars(resolveColors(DEFAULT_DARK_PALETTE, []));
    mark("shell-mounted");

    // The terminal panes are already in the tree and mount now — emitting term-ready, which spawns
    // claude — without waiting on Monaco. The editor (and its VSCode service layer) is a separate chunk
    // loaded here, off the first-paint path; the pane shows a placeholder until it resolves. A failure
    // must not take down the shell, so we catch and let the terminals keep working.
    // Hold the splash over everything until the editor is ready, then fade once — so the editor's first
    // paint happens *under* the splash and the reveal shows a settled UI (no placeholder relay, no
    // pop-in flash). A safety timeout dismisses it even if the editor chunk is slow or fails, so the
    // terminals are never stuck behind it.
    const splashFallback = window.setTimeout(dismissSplash, 3000);
    void import("./editor/editor-host")
      .then(({ createEditorHost }) => createEditorHost(editorContainer))
      .then((editorHost) => {
        host = editorHost;
        if (pendingOpen !== undefined) {
          editorHost.openFile(pendingOpen.path, pendingOpen.content, pendingOpen.line);
          pendingOpen = undefined;
        }
        postToHost({ type: "monaco-ready" });
        mark("editor-ready");
      })
      .catch((error: unknown) => log("error", `editor init failed: ${String(error)}`))
      .finally(() => {
        window.clearTimeout(splashFallback);
        dismissSplash();
      });

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

    // Ctrl+1..9 jumps focus to the Nth pane (DFS order). Capture phase so it wins over the focused
    // xterm/Monaco; only intercepted when a pane exists at that index, so other Ctrl+digit chords pass
    // through to the terminal/editor untouched.
    const onKeyDown = (event: KeyboardEvent): void => {
      if (!event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) {
        return;
      }
      if (event.key.length !== 1 || event.key < "1" || event.key > "9") {
        return;
      }
      const kind = paneNumbers()[Number(event.key) - 1];
      if (kind === undefined) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      focusPane(kind);
    };
    window.addEventListener("keydown", onKeyDown, { capture: true });

    // Track which pane holds focus (by click, Ctrl+N, or tab) for the active highlight.
    const onFocusIn = (event: FocusEvent): void => {
      const slot = (event.target as HTMLElement | null)?.closest("[data-kind]");
      setFocusedKind(slot?.getAttribute("data-kind") ?? null);
    };
    document.addEventListener("focusin", onFocusIn);

    onCleanup(() => {
      window.clearInterval(hudTimer);
      window.clearTimeout(autoBench);
      window.clearTimeout(persistTimer);
      window.removeEventListener("keydown", onKeyDown, { capture: true });
      document.removeEventListener("focusin", onFocusIn);
      offHost();
      meter.dispose();
      load.stop();
      host?.editor.dispose();
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
      <LayoutView root={layoutRoot()} renderPane={renderPane} onResize={onLayoutResize} />
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
