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
import type { ChangeFile } from "./changes/ChangesPanel";
import { ResizeFrame } from "./chrome/ResizeFrame";
import { TitleBar } from "./chrome/TitleBar";
import { focusOmnibar } from "./chrome/omnibar-controller";
import { setContext } from "./commands/context";
import { installKeybindings } from "./commands/keybindings";
import { registerCommand } from "./commands/registry";
import { CommandIds } from "./commands/types";
import type { EditorHost } from "./editor/editor-host";
import { type InlineDiff, createInlineDiff } from "./editor/inline-diff";
import type { DirListings } from "./files/FileBrowser";
import { runFpsProbe } from "./latency/fps-probe";
import { LatencyMeter } from "./latency/latency-meter";
import { LoadGenerator } from "./latency/load-generator";
import type { BenchmarkReport, LatencySummary, LiveLatencyStats } from "./latency/types";
import { LayoutView } from "./layout/LayoutView";
import { paneOrder } from "./layout/geometry";
import { layoutDocument, sendLayout } from "./layout/store";
import type { LayoutNode } from "./layout/types";
import { type Toast, Toasts } from "./notify/Toasts";
import { dismissSplash } from "./splash";
import { mark } from "./startup-timing";
import { TerminalView } from "./terminal/TerminalView";
import { DEFAULT_DARK_PALETTE, applyColorsToCssVars, resolveColors } from "./theme";

// The Monaco editor and its VSCode service layer are the heaviest code in the app. They live behind a
// dynamic import (see onMount → editor-host) so the entry chunk — and thus first paint — carries none of it.
const ChangesPanel = lazy(() => import("./changes/ChangesPanel"));
const FileBrowser = lazy(() => import("./files/FileBrowser"));

// The session's workspace root (host-injected before navigation); the file browser's tree is rooted
// here. Null in plain-browser dev (no host) — the browser toggle is hidden in that case.
const WORKSPACE_ROOT = window.__WEAVIE_LSP__?.workspace ?? null;

// Host-injected shell config (Windows custom title bar). Absent on macOS / plain-browser dev, where the
// web title bar isn't rendered and the floating Files/Changes buttons remain the panel toggles.
const SHELL = window.__WEAVIE_SHELL__;
const CUSTOM_TITLEBAR = SHELL?.titleBar === "custom";

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
  const [changeFiles, setChangeFiles] = createSignal<ChangeFile[]>([]);
  const [changesOpen, setChangesOpen] = createSignal(false);
  const [dirListings, setDirListings] = createSignal<DirListings>({});
  const [browserOpen, setBrowserOpen] = createSignal(false);
  // The file currently shown in the editor, tracked so the browser can highlight + reveal it.
  const [currentFile, setCurrentFile] = createSignal<string | null>(null);
  // User-facing toasts (e.g. an autosave write that failed). Auto-dismissed after a few seconds, or by
  // the user; a failed save surfaces here rather than being silently dropped.
  const [toasts, setToasts] = createSignal<Toast[]>([]);
  let nextToastId = 0;
  const dismissToast = (id: number): void => {
    setToasts((list) => list.filter((t) => t.id !== id));
  };
  const addToast = (level: Toast["level"], message: string): void => {
    const id = ++nextToastId;
    setToasts((list) => [...list, { id, level, message }]);
    window.setTimeout(() => dismissToast(id), 6000);
  };
  // Custom title bar state: window chrome (maximize glyph + blur dim) pushed by the host, and the flat
  // workspace file index the omnibar's "Go to File" filters over (root may differ from WORKSPACE_ROOT).
  const [maximized, setMaximized] = createSignal(false);
  const [windowFocused, setWindowFocused] = createSignal(true);
  const [fileIndex, setFileIndex] = createSignal<string[]>([]);
  const [indexRoot, setIndexRoot] = createSignal<string | null>(WORKSPACE_ROOT);
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

  // The openDiff currently under inline review (default mode). openDiff blocks per-edit, so at most one is
  // live at a time — a plain var (not a signal) is enough; the controller's toolbar drives accept/reject.
  let activeReview: { id: string; path: string; original: string } | undefined;

  const resolveReview = (keep: boolean): void => {
    const review = activeReview;
    if (review === undefined) {
      return;
    }
    activeReview = undefined;
    // endReview reverts the buffer to `original` on reject and returns the final content; on keep that's the
    // (possibly tweaked) proposal Claude then writes on FILE_SAVED.
    const finalContents = host?.endReview(review.path, keep, review.original) ?? "";
    inlineDiff?.clear(review.path);
    postToHost({
      type: "diff-resolved",
      id: review.id,
      kept: keep,
      finalContents: keep ? finalContents : "",
    });
  };

  const meter = new LatencyMeter();
  const load = new LoadGenerator(5);
  // The Monaco editor host, set once its chunk has loaded and the editor is created (see onMount).
  let host: EditorHost | undefined;
  // The inline-diff controller (Cursor-style diff inside the live editor), created with the host's editor.
  let inlineDiff: InlineDiff | undefined;
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
    setCurrentFile(path);
    if (host !== undefined) {
      host.openFile(path, content, line);
    } else {
      // Editor chunk not loaded yet — remember the request and replay it once the host is ready.
      pendingOpen = { path, content, line };
    }
  };

  const toggleBrowser = (): void => {
    setBrowserOpen((open) => !open);
  };

  // Whenever the browser is open and the workspace root listing hasn't loaded, request it; the current
  // file's ancestor folders then cascade open from there. Driven by state, so it loads however it opened.
  createEffect(() => {
    if (browserOpen() && WORKSPACE_ROOT !== null && dirListings()[WORKSPACE_ROOT] === undefined) {
      postToHost({ type: "list-dir", path: WORKSPACE_ROOT });
    }
  });

  onMount(() => {
    // Theme chrome from the default palette (spec §6 application surface). Override ops layer here once
    // wired to settings/MCP; for now this publishes --weavie-* CSS vars for the chrome to consume.
    applyColorsToCssVars(resolveColors(DEFAULT_DARK_PALETTE, []));
    mark("shell-mounted");

    // The terminal panes are already in the tree and mount now — emitting term-ready, which spawns
    // claude — without waiting on Monaco. The editor (and its VSCode service layer) is a separate chunk
    // loaded here, off the first-paint path; the pane shows a placeholder until it resolves.
    // The splash is held over everything until the editor is ready, then faded once — so the editor's
    // first paint happens *under* the splash and the reveal shows a settled UI. We fade it on a
    // DETERMINISTIC outcome only: editor ready, or a real failure. A failure — chunk load, editor crash,
    // or an init that never settles within EDITOR_INIT_MS — rejects LOUDLY (logged as an error), then
    // frees the splash so the already-working terminals aren't trapped. No silent timer that dismisses
    // while pretending success.
    const EDITOR_INIT_MS = 15_000; // generous: only a genuine hang trips it, not a slow cold start.
    let initTimer: number | undefined;
    const editorReady = import("./editor/editor-host").then(({ createEditorHost }) =>
      createEditorHost(editorContainer),
    );
    const initDeadline = new Promise<never>((_, reject) => {
      initTimer = window.setTimeout(
        () => reject(new Error(`editor init did not settle within ${EDITOR_INIT_MS}ms`)),
        EDITOR_INIT_MS,
      );
    });
    void Promise.race([editorReady, initDeadline])
      .then((created) => {
        host = created;
        inlineDiff = createInlineDiff(created.editor);
        if (pendingOpen !== undefined) {
          created.openFile(pendingOpen.path, pendingOpen.content, pendingOpen.line);
          pendingOpen = undefined;
        }
        postToHost({ type: "monaco-ready" });
        mark("editor-ready");
      })
      .catch((error: unknown) => log("error", `editor init failed: ${String(error)}`))
      .finally(() => {
        window.clearTimeout(initTimer);
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
        // Render Claude's openDiff proposal INLINE in the live editor (no standalone diff viewer): the buffer
        // shows `proposed` (autosave suppressed), with the diff vs `original` and a Keep/Reject toolbar.
        activeReview = { id: message.id, path: message.path, original: message.original };
        host?.beginReview(message.path, message.proposed, 1);
        inlineDiff?.set(message.path, {
          original: message.original,
          mode: "review",
          onAccept: () => resolveReview(true),
          onReject: () => resolveReview(false),
        });
      } else if (message.type === "close-diff") {
        // Host cancelled the openDiff: tear the review down (revert buffer) without replying — the host's
        // awaiting task is already cancelled.
        if (activeReview?.id === message.id) {
          host?.endReview(activeReview.path, false, activeReview.original);
          inlineDiff?.clear(activeReview.path);
          activeReview = undefined;
        }
      } else if (message.type === "open-file") {
        openFileInEditor(message.path, message.content, message.line);
      } else if (message.type === "refresh-file") {
        // Claude edited this file (any permission mode) — live-update its model in place if open.
        host?.applyExternalEdit(message.path, message.content);
      } else if (message.type === "turn-diff") {
        // Inline diff of this turn's changes, shown in the live editor. Equal baseline/current = no markers.
        if (message.baseline === message.current) {
          inlineDiff?.clear(message.path);
        } else {
          inlineDiff?.set(message.path, {
            original: message.baseline,
            mode: "applied",
            onAccept: () => postToHost({ type: "accept-turn" }),
            onUndo: () => postToHost({ type: "undo-turn" }),
          });
        }
      } else if (message.type === "turn-reset") {
        inlineDiff?.clearAll();
      } else if (message.type === "notify") {
        addToast(message.level, message.message);
      } else if (message.type === "session-changes") {
        setChangeFiles(message.files);
      } else if (message.type === "change-diff") {
        // A file picked in the Changes navigator: show its whole-session diff inline in the live editor
        // (read-only view — the file is opened via reveal-file alongside this request).
        if (message.baseline === message.current) {
          inlineDiff?.clear(message.path);
        } else {
          inlineDiff?.set(message.path, { original: message.baseline, mode: "view" });
        }
      } else if (message.type === "dir-listing") {
        setDirListings((prev) => ({ ...prev, [message.path]: message.entries }));
      } else if (message.type === "window-state") {
        setMaximized(message.maximized);
        setWindowFocused(message.focused);
      } else if (message.type === "file-index") {
        setIndexRoot(message.root);
        setFileIndex(message.files);
      }
    });

    // Commands: register the web-side handlers, then install the capture-phase keybinding resolver. The
    // migrated Ctrl+1–9 (focus pane by index), the omnibar focus shortcuts, and the view toggles all resolve
    // through it; Core commands (reopen terminal) route to the host. See docs/specs/commands.md.
    const offCommands = [
      // focus-pane-by-index returns false when there's no pane at that number, so an unbound Ctrl+digit
      // still falls through to the focused xterm/Monaco (preserving the old behavior).
      registerCommand(CommandIds.focusPaneByIndex, (args) => {
        const index = Number((args as { index?: unknown } | undefined)?.index);
        if (!Number.isFinite(index)) {
          return false;
        }
        const kind = paneNumbers()[index - 1];
        if (kind === undefined) {
          return false;
        }
        focusPane(kind);
        return true;
      }),
      registerCommand(CommandIds.toggleFileBrowser, () => toggleBrowser()),
      registerCommand(CommandIds.toggleChanges, () => setChangesOpen((open) => !open)),
      registerCommand(CommandIds.focusOmnibarFiles, () => focusOmnibar("file")),
      registerCommand(CommandIds.focusOmnibarCommands, () => focusOmnibar("command")),
    ];
    const offKeybindings = installKeybindings();

    // Track which pane holds focus (by click, Ctrl+N, or tab) for the active highlight, and publish it as a
    // `when`-context key so command guards (e.g. terminalFocused) can read it.
    const onFocusIn = (event: FocusEvent): void => {
      const slot = (event.target as HTMLElement | null)?.closest("[data-kind]");
      const kind = slot?.getAttribute("data-kind") ?? null;
      setFocusedKind(kind);
      setContext("focusedPane", kind);
      setContext("editorFocused", kind === "editor");
      setContext("terminalFocused", kind?.startsWith("terminal:") ?? false);
    };
    document.addEventListener("focusin", onFocusIn);

    onCleanup(() => {
      window.clearInterval(hudTimer);
      window.clearTimeout(autoBench);
      window.clearTimeout(persistTimer);
      offKeybindings();
      for (const off of offCommands) {
        off();
      }
      document.removeEventListener("focusin", onFocusIn);
      offHost();
      meter.dispose();
      load.stop();
      inlineDiff?.dispose();
      host?.editor.dispose();
    });
  });

  return (
    <div class="app">
      <Show when={CUSTOM_TITLEBAR}>
        <TitleBar
          maximized={maximized()}
          focused={windowFocused()}
          files={fileIndex()}
          root={indexRoot()}
          currentFile={currentFile()}
          onWindowControl={(action) => postToHost({ type: "window-control", action })}
          onMenuAction={(action, path) =>
            postToHost(
              path === undefined
                ? { type: "menu-action", action }
                : { type: "menu-action", action, path },
            )
          }
          onToggleFiles={toggleBrowser}
          onToggleChanges={() => setChangesOpen((open) => !open)}
          onOpenFile={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
          onRequestIndex={() => postToHost({ type: "request-file-index" })}
        />
      </Show>
      <Show when={CUSTOM_TITLEBAR}>
        <ResizeFrame maximized={maximized()} />
      </Show>
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
      <Show when={changeFiles().length > 0 && !CUSTOM_TITLEBAR}>
        <button
          type="button"
          class="changes-toggle"
          onClick={() => setChangesOpen((open) => !open)}
        >
          Changes {changeFiles().length}
        </button>
      </Show>
      <Show when={changesOpen()}>
        <Suspense>
          <ChangesPanel
            files={changeFiles()}
            currentFile={currentFile()}
            onSelect={(path) => {
              // Open the file in the live editor and request its session diff, shown inline (no diff viewer).
              postToHost({ type: "reveal-file", path, line: 1 });
              postToHost({ type: "get-change-diff", path });
            }}
            onClose={() => setChangesOpen(false)}
          />
        </Suspense>
      </Show>
      <Show when={WORKSPACE_ROOT !== null && !CUSTOM_TITLEBAR}>
        <button type="button" class="browser-toggle" onClick={toggleBrowser}>
          Files
        </button>
      </Show>
      <Show when={browserOpen() && WORKSPACE_ROOT !== null}>
        <Suspense>
          <FileBrowser
            root={WORKSPACE_ROOT!}
            listings={dirListings()}
            currentFile={currentFile()}
            onExpand={(path) => postToHost({ type: "list-dir", path })}
            onOpen={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
            onClose={() => setBrowserOpen(false)}
          />
        </Suspense>
      </Show>
      <Toasts toasts={toasts()} onDismiss={dismissToast} />
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
