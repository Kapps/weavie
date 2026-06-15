import type { LatencySummary } from "./types";

const EMPTY: LatencySummary = { count: 0, mean: 0, p50: 0, p95: 0, p99: 0, max: 0 };

/** Nearest-rank percentile over already-sorted ascending samples. */
function percentile(sorted: readonly number[], fraction: number): number {
  if (sorted.length === 0) {
    return 0;
  }
  const rank = Math.ceil(fraction * sorted.length);
  const index = Math.min(sorted.length - 1, Math.max(0, rank - 1));
  return sorted[index] ?? 0;
}

export function summarize(samples: readonly number[]): LatencySummary {
  if (samples.length === 0) {
    return EMPTY;
  }
  const sorted = [...samples].sort((a, b) => a - b);
  let sum = 0;
  for (const value of sorted) {
    sum += value;
  }
  return {
    count: sorted.length,
    mean: sum / sorted.length,
    p50: percentile(sorted, 0.5),
    p95: percentile(sorted, 0.95),
    p99: percentile(sorted, 0.99),
    max: sorted[sorted.length - 1] ?? 0,
  };
}

/** A fixed-capacity rolling window of samples. */
export class RollingWindow {
  private readonly values: number[] = [];

  constructor(private readonly capacity: number) {}

  push(value: number): void {
    this.values.push(value);
    if (this.values.length > this.capacity) {
      this.values.shift();
    }
  }

  clear(): void {
    this.values.length = 0;
  }

  summary(): LatencySummary {
    return summarize(this.values);
  }
}
