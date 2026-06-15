import * as monaco from "monaco-editor";
import type { LoadGenerator } from "./load-generator";
import { summarize } from "./percentiles";
import type { BenchmarkConfig, BenchmarkPhaseResult, BenchmarkReport } from "./types";

// Reproducible, headless-friendly latency benchmark. Unlike the live meter (which needs a
// human at the keyboard), this drives Monaco's real edit pipeline programmatically and
// measures edit-apply -> next frame. It is explicitly RENDER latency (no hardware input),
// the dominant controllable component; the live Event-Timing path covers true input->paint.
// Runs an idle phase and an under-load phase so the tail shows up.

const FRAME_TIMEOUT_MS = 200;

// rAF, but races a timeout so the benchmark never hangs when the window is occluded
// (lock screen / display asleep) and rAF is paused. `viaRaf: false` => a real frame
// never came; those samples are not render latency and must be excluded.
const nextFrameOrTimeout = (): Promise<{ t: number; viaRaf: boolean }> =>
  new Promise((resolve) => {
    let settled = false;
    requestAnimationFrame((t) => {
      if (!settled) {
        settled = true;
        resolve({ t, viaRaf: true });
      }
    });
    setTimeout(() => {
      if (!settled) {
        settled = true;
        resolve({ t: performance.now(), viaRaf: false });
      }
    }, FRAME_TIMEOUT_MS);
  });

const delay = (ms: number): Promise<void> =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

const CHARS = "abcdefghijklmnopqrstuvwxyz(){};= ";

function pickText(index: number): string {
  // Mostly characters, with a newline every ~40 keystrokes to keep tokenizer/bracket
  // colorization realistically busy without the line growing without bound.
  if (index > 0 && index % 40 === 0) {
    return "\n  const sample = ";
  }
  return CHARS[index % CHARS.length] ?? "x";
}

class FrameIntervalTracker {
  private readonly intervals: number[] = [];
  private last = 0;
  private running = false;

  start(): void {
    this.running = true;
    const tick = (t: number): void => {
      if (!this.running) {
        return;
      }
      if (this.last !== 0) {
        this.intervals.push(t - this.last);
      }
      this.last = t;
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }

  stop(): number[] {
    this.running = false;
    return this.intervals;
  }
}

async function runPhase(
  editor: monaco.editor.IStandaloneCodeEditor,
  label: string,
  loadActive: boolean,
  config: BenchmarkConfig,
): Promise<BenchmarkPhaseResult> {
  const model = editor.getModel();
  if (model === null) {
    throw new Error("benchmark: editor has no model");
  }

  const samples: number[] = [];
  const frames = new FrameIntervalTracker();
  frames.start();

  // Let the phase settle (especially the load generator spinning up).
  await delay(150);

  // Warm up the edit path (first edit triggers Monaco worker spin-up / JIT and
  // produces a one-off multi-hundred-ms outlier). These keystrokes are not measured.
  for (let w = 0; w < 12; w++) {
    const end = model.getFullModelRange().getEndPosition();
    editor.executeEdits("warmup", [
      {
        range: new monaco.Range(end.lineNumber, end.column, end.lineNumber, end.column),
        text: "x",
        forceMoveMarkers: true,
      },
    ]);
    const warm = await nextFrameOrTimeout();
    if (!warm.viaRaf && w >= 2) {
      break; // not rendering — don't waste the warmup budget
    }
  }

  let fallbacks = 0;
  for (let i = 0; i < config.keystrokes; i++) {
    const end = model.getFullModelRange().getEndPosition();
    const range = new monaco.Range(end.lineNumber, end.column, end.lineNumber, end.column);
    const text = pickText(i);

    const t0 = performance.now();
    editor.executeEdits("bench", [{ range, text, forceMoveMarkers: true }]);
    const frame = await nextFrameOrTimeout();
    if (frame.viaRaf) {
      samples.push(frame.t - t0);
    } else {
      fallbacks++;
    }

    // Bail early if the view clearly isn't rendering (occluded / display asleep).
    if (i >= 20 && fallbacks > (i + 1) * 0.8) {
      break;
    }

    await delay(config.intervalMs);
  }

  const intervals = frames.stop();
  const frameSummary = summarize(intervals);
  // Throttled if rAF barely fired, the cadence is slow (<~30fps), or too few real samples.
  const framesLookThrottled =
    frameSummary.count < 20 || frameSummary.p50 > 32 || samples.length < 20;

  return {
    label,
    loadActive,
    editToFrame: summarize(samples),
    frameIntervalMs: frameSummary,
    framesLookThrottled,
  };
}

export async function runBenchmark(
  editor: monaco.editor.IStandaloneCodeEditor,
  load: LoadGenerator,
  config: BenchmarkConfig,
): Promise<BenchmarkReport> {
  const phases: BenchmarkPhaseResult[] = [];

  load.stop();
  phases.push(await runPhase(editor, "idle", false, config));

  load.start();
  phases.push(await runPhase(editor, "under-load", true, config));
  load.stop();

  const idle = phases[0];
  const displayHz =
    idle && idle.frameIntervalMs.p50 > 0 ? Math.round(1000 / idle.frameIntervalMs.p50) : 0;

  const throttled = phases.some((p) => p.framesLookThrottled);
  const note = throttled
    ? "Frame cadence looked throttled (window likely occluded or display asleep) — numbers are NOT reliable; re-run with the window frontmost on an awake display."
    : "Synthetic edit-apply -> next-frame (render latency). True input->paint is the live Event-Timing meter; display scanout excluded.";

  return { config, displayHz, phases, note };
}
