// Shared latency types.

/** Percentile summary over a set of millisecond samples (nearest-rank). */
export interface LatencySummary {
  count: number;
  mean: number;
  p50: number;
  p95: number;
  p99: number;
  max: number;
}

/** Live, rolling-window stats shown in the HUD and pushed to the host. */
export interface LiveLatencyStats {
  /** Fine-grained keydown -> next animation frame (frame-start), excludes display scanout. */
  inputToFrame: LatencySummary;
  /** Event Timing API keydown duration: input -> next paint (browser-reported, ~8ms buckets). */
  inputToPaint: LatencySummary;
  /** Per-keystroke main-thread handler cost (processingEnd - processingStart). */
  handler: LatencySummary;
  /** Observed animation-frame interval; ~8.3ms == 120Hz, ~16.7ms == 60Hz. */
  frameIntervalMs: LatencySummary;
  loadActive: boolean;
}

export interface BenchmarkConfig {
  /** Keystrokes per phase. */
  keystrokes: number;
  /** Milliseconds between synthetic keystrokes (cadence of a fast typist ~10/s = 100ms). */
  intervalMs: number;
}

export interface BenchmarkPhaseResult {
  label: string;
  loadActive: boolean;
  /** Synthetic edit-apply -> next frame (render latency). */
  editToFrame: LatencySummary;
  frameIntervalMs: LatencySummary;
  /** True if the frame cadence looked throttled (window occluded / display asleep). */
  framesLookThrottled: boolean;
}

export interface BenchmarkReport {
  config: BenchmarkConfig;
  displayHz: number;
  phases: BenchmarkPhaseResult[];
  note: string;
}
