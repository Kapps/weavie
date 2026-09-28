import { normalizePath } from "../fs-path";

export interface ReviewHorizontalPosition {
  get(): number;
  set(left: number): void;
  subscribe(changed: () => void): () => void;
  bindRange(): { update(maximum: number): void; dispose(): void };
}

interface MeasuredRange {
  maximum: number | undefined;
}

/** One immutable snapshot per horizontal change, shared by passive and live presentations. */
export class ReviewHorizontalPositions {
  private values: Readonly<Record<string, number>> = {};
  private readonly listeners = new Map<string, Set<() => void>>();
  private readonly ranges = new Map<string, Set<MeasuredRange>>();

  public constructor(private readonly changed: () => void) {}

  private clamp(key: string, left: number): number {
    let maximum = Number.POSITIVE_INFINITY;
    for (const range of this.ranges.get(key) ?? []) {
      if (range.maximum !== undefined) maximum = range.maximum;
    }
    return Math.min(maximum, Math.max(0, Math.trunc(left)));
  }

  private publish(keys: Iterable<string>): void {
    try {
      for (const key of keys) for (const listener of this.listeners.get(key) ?? []) listener();
    } finally {
      this.changed();
    }
  }

  public snapshot(): Readonly<Record<string, number>> {
    return this.values;
  }

  public restore(values: Readonly<Record<string, number>>): void {
    const next: Record<string, number> = {};
    for (const [path, left] of Object.entries(values)) {
      const key = normalizePath(path);
      const value = this.clamp(key, left);
      if (value !== 0) next[key] = value;
    }
    const keys = new Set([...Object.keys(this.values), ...Object.keys(next)]);
    const changed = [...keys].filter((key) => (this.values[key] ?? 0) !== (next[key] ?? 0));
    if (changed.length === 0) return;
    this.values = next;
    this.publish(changed);
  }

  public forPath(path: string): ReviewHorizontalPosition {
    const key = normalizePath(path);
    const get = (): number => (Object.hasOwn(this.values, key) ? this.values[key]! : 0);
    const set = (left: number): void => {
      const value = this.clamp(key, left);
      if (get() === value) return;
      const next = { ...this.values };
      if (value === 0) delete next[key];
      else next[key] = value;
      this.values = next;
      this.publish([key]);
    };
    return {
      get,
      set,
      bindRange: () => {
        const ranges = this.ranges.get(key) ?? new Set<MeasuredRange>();
        this.ranges.set(key, ranges);
        const range: MeasuredRange = { maximum: undefined };
        ranges.add(range);
        let disposed = false;
        return {
          update: (maximum) => {
            if (disposed) throw new Error("The horizontal range no longer belongs to this file");
            range.maximum = Math.max(0, Math.trunc(maximum));
            set(get());
          },
          dispose: () => {
            if (disposed) return;
            disposed = true;
            ranges.delete(range);
            if (ranges.size === 0) this.ranges.delete(key);
            set(get());
          },
        };
      },
      subscribe: (changed) => {
        const listeners = this.listeners.get(key) ?? new Set();
        this.listeners.set(key, listeners);
        listeners.add(changed);
        return () => {
          listeners.delete(changed);
          if (listeners.size === 0) this.listeners.delete(key);
        };
      },
    };
  }
}
