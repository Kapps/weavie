// A trailing-edge debounce: each call restarts the timer, so `fn` runs once the calls settle. The returned
// function carries `cancel()` (drop a pending call — e.g. on teardown) and `flush()` (run it now, before a
// switch). The latest arguments win, so callers that capture per-call state pass it as an argument rather
// than closing over a mutable variable.
export interface Debounced<A extends unknown[]> {
  (...args: A): void;
  cancel(): void;
  flush(): void;
}

export function debounce<A extends unknown[]>(fn: (...args: A) => void, ms: number): Debounced<A> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  let pending: A | undefined;
  const fire = (): void => {
    timer = undefined;
    const args = pending as A;
    pending = undefined;
    fn(...args);
  };
  const run = ((...args: A): void => {
    pending = args;
    if (timer !== undefined) {
      clearTimeout(timer);
    }
    timer = setTimeout(fire, ms);
  }) as Debounced<A>;
  run.cancel = (): void => {
    if (timer !== undefined) {
      clearTimeout(timer);
      timer = undefined;
    }
    pending = undefined;
  };
  run.flush = (): void => {
    if (timer !== undefined) {
      clearTimeout(timer);
      fire();
    }
  };
  return run;
}
