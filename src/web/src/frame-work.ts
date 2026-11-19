// Work that runs right after a frame has drawn, so it has the rest of that frame to itself: queued stages one
// per frame, and idle callbacks in the frames no stage took.

// An idle callback's share of a frame.
const IDLE_BUDGET_MS = 10;
// Idle work waits this long after the last scroll input, unless its timeout comes due first.
const SETTLE_MS = 250;

interface IdleEntry {
  id: number;
  callback: IdleRequestCallback;
  timer: ReturnType<typeof setTimeout> | undefined;
}

interface Stage {
  wanted(): boolean;
  run(): void;
}

const stages: Stage[] = [];
const idle: IdleEntry[] = [];
let scheduled = false;
let lastScroll = Number.NEGATIVE_INFINITY;
let drawn: MessageChannel | undefined;

const take = (entry: IdleEntry): IdleEntry => {
  clearTimeout(entry.timer);
  idle.splice(idle.indexOf(entry), 1);
  return entry;
};

function pump(): void {
  scheduled = false;
  try {
    let stage = stages.shift();
    // A stage nobody wants any more is let go without taking the frame.
    while (stage !== undefined && !stage.wanted()) {
      stage.run();
      stage = stages.shift();
    }
    if (stage !== undefined) {
      stage.run();
    } else if (performance.now() - lastScroll >= SETTLE_MS) {
      const end = performance.now() + IDLE_BUDGET_MS;
      const deadline: IdleDeadline = {
        didTimeout: false,
        timeRemaining: () => Math.max(0, end - performance.now()),
      };
      // Only what was waiting: a callback that asks again gets a later frame.
      for (let waiting = idle.length; waiting > 0 && deadline.timeRemaining() > 0; waiting--) {
        take(idle[0]!).callback(deadline);
      }
    }
  } finally {
    // Also after a callback threw, so one failure doesn't strand the rest.
    if (stages.length > 0 || idle.length > 0) schedule();
  }
}

function schedule(): void {
  if (scheduled) return;
  scheduled = true;
  if (drawn === undefined) {
    drawn = new MessageChannel();
    drawn.port1.onmessage = pump;
  }
  // A message posted from the frame callback is handled once that frame has drawn.
  const { port2 } = drawn;
  requestAnimationFrame(() => port2.postMessage(0));
}

/**
 * Resolves right after a frame has drawn, for one caller per frame in the order they asked. A caller no longer
 * `wanted` by then resolves without taking a frame.
 */
export function afterFrame(wanted: () => boolean): Promise<void> {
  return new Promise((resolve) => {
    stages.push({ wanted, run: resolve });
    schedule();
  });
}

/** Marks the page as scrolling now, for a scroller that moves without wheel or touch events. */
export function noteScroll(): void {
  lastScroll = performance.now();
}

/**
 * Gives an engine without `requestIdleCallback` (WebKit) one that runs after a drawn frame, once scrolling has
 * settled ({@link noteScroll}, or a wheel or touch move). Monaco otherwise runs its idle work at once, in 15 ms
 * slices, whatever the page is doing.
 */
export function installIdleCallback(): void {
  if (typeof globalThis.requestIdleCallback === "function") return;
  let lastId = 0;
  globalThis.requestIdleCallback = (callback, options) => {
    const entry: IdleEntry = { id: ++lastId, callback, timer: undefined };
    if (options?.timeout !== undefined) {
      entry.timer = setTimeout(
        () => take(entry).callback({ didTimeout: true, timeRemaining: () => 0 }),
        options.timeout,
      );
    }
    idle.push(entry);
    schedule();
    return entry.id;
  };
  globalThis.cancelIdleCallback = (id) => {
    const entry = idle.find((candidate) => candidate.id === id);
    if (entry !== undefined) take(entry);
  };
  for (const type of ["wheel", "touchmove"]) {
    addEventListener(type, noteScroll, { capture: true, passive: true });
  }
}
