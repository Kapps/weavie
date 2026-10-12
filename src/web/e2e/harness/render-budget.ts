import type { Page } from "@playwright/test";

// Load-independent render guards: counts of what a code path DOES (forced-layout reads from Weavie's bundles,
// content widgets and zone cards added or removed), never how long it took, so a slow machine only delays
// a run and a regression fails with the offending call site or widget named.

/** What one probed window did. */
export interface RenderSample {
  /** Animation frames that ran. */
  frames: number;
  /** Layout-forcing reads whose stack reaches Weavie's own bundles, on the input and render path. */
  reads: number;
  /** The same reads from timer, idle and resize-observer callbacks (see `deferredPhase`). */
  deferred: number;
  /** Nodes attached to and detached from Monaco's content-widget containers and the zone-card layer. */
  added: number;
  removed: number;
  /** Read sites (`file:line:col apis  …source…`) and widget ids (`+id`/`-id`), most frequent first. */
  sites: [string, number][];
  deferredSites: [string, number][];
  widgets: [string, number][];
}

// https://gist.github.com/paulirish/5d52fb081b3570c81e3a — everything that can force a synchronous layout.
const FORCING = {
  Element: [
    "clientLeft",
    "clientTop",
    "clientWidth",
    "clientHeight",
    "scrollLeft",
    "scrollTop",
    "scrollWidth",
    "scrollHeight",
    "getClientRects",
    "getBoundingClientRect",
    "scrollBy",
    "scrollTo",
    "scrollIntoView",
    "scrollIntoViewIfNeeded",
    "computedRole",
    "computedName",
  ],
  HTMLElement: [
    "offsetLeft",
    "offsetTop",
    "offsetWidth",
    "offsetHeight",
    "offsetParent",
    "innerText",
    "focus",
  ],
  Range: ["getClientRects", "getBoundingClientRect"],
  SVGGraphicsElement: ["getBBox", "getCTM", "getScreenCTM"],
  Document: ["elementFromPoint", "elementsFromPoint", "scrollingElement"],
  MouseEvent: ["layerX", "layerY", "offsetX", "offsetY"],
  window: [
    "scrollX",
    "scrollY",
    "innerWidth",
    "innerHeight",
    "getComputedStyle",
    "scrollBy",
    "scrollTo",
  ],
};

/**
 * A `preNavigate` hook marking timer, idle and resize-observer callbacks from page load on. Their reads are counted
 * apart from the input and render path: debounced work runs in its own task, and an observer runs after layout.
 * Only the synchronous callback is marked; work after an `await` inside it counts as on-path.
 */
export const deferredPhase = {
  run: async (page: Page): Promise<void> => {
    await page.addInitScript(() => {
      const phase = window as unknown as { __deferredDepth: number } & Record<string, unknown>;
      phase.__deferredDepth = 0;
      const deferred =
        <A extends unknown[], R>(callback: (...args: A) => R) =>
        (...args: A): R => {
          phase.__deferredDepth += 1;
          try {
            return callback(...args);
          } finally {
            phase.__deferredDepth -= 1;
          }
        };
      for (const name of ["setTimeout", "setInterval", "requestIdleCallback"]) {
        const original = phase[name] as (callback: unknown, ...rest: unknown[]) => number;
        phase[name] = (callback: unknown, ...rest: unknown[]) =>
          original(
            typeof callback === "function" ? deferred(callback as () => unknown) : callback,
            ...rest,
          );
      }
      const Observer = window.ResizeObserver;
      window.ResizeObserver = class extends Observer {
        constructor(callback: ResizeObserverCallback) {
          super(deferred(callback));
        }
      };
    });
  },
};

interface PageProbe {
  reads(): number;
  stop(): Promise<RenderSample>;
}

/**
 * Counts what `step` does over `iterations` runs, restoring every patched API afterwards. Stops early once the
 * reads pass `readBudget`, so a regression that slows each step to a crawl fails on its count, not a timeout.
 */
export async function measureRender(
  page: Page,
  iterations: number,
  readBudget: number,
  step: () => Promise<void>,
): Promise<RenderSample> {
  await page.evaluate((forcing) => {
    type Sites = Map<string, { count: number; apis: Set<string> }>;
    const sites: Sites = new Map();
    const deferredSites: Sites = new Map();
    const widgets = new Map<string, number>();
    const state = {
      frames: 0,
      reads: 0,
      deferred: 0,
      added: 0,
      removed: 0,
      running: true,
    };
    const phase = window as unknown as { __deferredDepth?: number };
    if (phase.__deferredDepth === undefined)
      throw new Error("measureRender needs `test.use({ preNavigate: deferredPhase })`");
    // Weavie's own chunks, not the Monaco vendor chunk; the harness's locator reads have no /assets/ frame.
    const appFrame = /(\/assets\/(?!monaco-[\w-]{8}\.js)[^\s)]+):(\d+):(\d+)/;
    const stackLimit = Error.stackTraceLimit;
    // Deep enough that a Weavie call forcing layout through Monaco's own stack is still attributed.
    Error.stackTraceLimit = 40;
    const note = (api: string): void => {
      const stack = (new Error().stack ?? "").split("\n").slice(3);
      const site = stack.map((frame) => appFrame.exec(frame)).find((match) => match !== null);
      if (!site) return;
      const deferred = phase.__deferredDepth! > 0;
      state[deferred ? "deferred" : "reads"] += 1;
      const into = deferred ? deferredSites : sites;
      const key = `${site[1]}\u0000${site[2]}\u0000${site[3]}`;
      const entry = into.get(key) ?? { count: 0, apis: new Set<string>() };
      entry.count += 1;
      entry.apis.add(api);
      into.set(key, entry);
    };
    const restore: (() => void)[] = [];
    const patch = (target: object, name: string): void => {
      let holder: object | null = target;
      while (holder !== null && !Object.hasOwn(holder, name))
        holder = Object.getPrototypeOf(holder);
      const descriptor =
        holder === null ? undefined : Object.getOwnPropertyDescriptor(holder, name);
      if (holder === null || descriptor === undefined) return;
      const counted = (original: (...args: unknown[]) => unknown) =>
        function (this: unknown, ...args: unknown[]) {
          note(name);
          return original.apply(this, args);
        };
      const { get, set, value } = descriptor;
      Object.defineProperty(holder, name, {
        ...descriptor,
        ...(typeof value === "function" ? { value: counted(value) } : {}),
        ...(get === undefined ? {} : { get: counted(get) }),
        ...(set === undefined ? {} : { set: counted(set) }),
      });
      restore.push(() => Object.defineProperty(holder, name, descriptor));
    };
    for (const [owner, names] of Object.entries(forcing)) {
      const scope = window as unknown as Record<string, { prototype: object } | undefined>;
      const target = owner === "window" ? window : scope[owner]?.prototype;
      if (target !== undefined) for (const name of names) patch(target, name);
    }
    const tally = (records: MutationRecord[]): void => {
      for (const record of records) {
        const container = record.target as Element;
        const layers = ".contentWidgets, .overflowingContentWidgets, .weavie-zone-cards-clip";
        if (!container.matches?.(layers)) continue;
        for (const [sign, nodes] of [
          ["+", record.addedNodes],
          ["-", record.removedNodes],
        ] as const) {
          for (const node of nodes) {
            const element = node as Element;
            const id = element.getAttribute?.("widgetId") || element.className || node.nodeName;
            const key = `${sign}${id.replace(/\d+/g, "#")}`;
            state[sign === "+" ? "added" : "removed"] += 1;
            widgets.set(key, (widgets.get(key) ?? 0) + 1);
          }
        }
      }
    };
    const observer = new MutationObserver(tally);
    observer.observe(document.body, { childList: true, subtree: true });
    const tick = (): void => {
      if (!state.running) return;
      state.frames += 1;
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
    const probe: PageProbe = {
      reads: () => state.reads,
      stop: async () => {
        state.running = false;
        tally(observer.takeRecords());
        observer.disconnect();
        for (const undo of restore) undo();
        Error.stackTraceLimit = stackLimit;
        const sources = new Map<string, Promise<string[]>>();
        const source = (url: string): Promise<string[]> => {
          if (!sources.has(url))
            sources.set(
              url,
              fetch(url).then(async (response) => (await response.text()).split("\n")),
            );
          return sources.get(url)!;
        };
        // Minified names say nothing, so each site carries the source around the read instead.
        const describe = (from: Sites): Promise<[string, number][]> =>
          Promise.all(
            [...from]
              .sort((a, b) => b[1].count - a[1].count)
              .slice(0, 8)
              .map(async ([key, { count, apis }]): Promise<[string, number]> => {
                const [url = "", line, column] = key.split("\u0000");
                const text = (await source(url))[Number(line) - 1] ?? "";
                const at = Number(column) - 1;
                const file = url.slice(url.lastIndexOf("/") + 1);
                return [
                  `${file}:${line}:${column} ${[...apis].join("/")}  …${text.slice(Math.max(0, at - 70), at + 50)}…`,
                  count,
                ];
              }),
          );
        return {
          frames: state.frames,
          reads: state.reads,
          deferred: state.deferred,
          added: state.added,
          removed: state.removed,
          sites: await describe(sites),
          deferredSites: await describe(deferredSites),
          widgets: [...widgets].sort((a, b) => b[1] - a[1]),
        };
      },
    };
    (window as unknown as { __renderProbe: PageProbe }).__renderProbe = probe;
  }, FORCING);
  const stop = (): Promise<RenderSample> =>
    page.evaluate(() => (window as unknown as { __renderProbe: PageProbe }).__renderProbe.stop());
  try {
    for (let index = 0; index < iterations; index++) {
      await step();
      const reads = await page.evaluate(() =>
        (window as unknown as { __renderProbe: PageProbe }).__renderProbe.reads(),
      );
      if (reads > readBudget) break;
    }
  } catch (error) {
    // Restores the patched APIs before the failure propagates.
    await stop();
    throw error;
  }
  return stop();
}

/** A failure message naming the top read sites and widget/card churn, so a red run points at the culprit. */
export function describeSample(sample: RenderSample): string {
  const lines = (entries: [string, number][]): string =>
    entries.map(([site, count]) => `  ${String(count).padStart(5)}  ${site}`).join("\n") ||
    "  (none)";
  return [
    `${sample.reads} Weavie forced-layout reads (+${sample.deferred} deferred), ${sample.added} widgets/cards added, ${sample.removed} removed, ${sample.frames} frames.`,
    `Top read sites:\n${lines(sample.sites)}`,
    `Top deferred read sites:\n${lines(sample.deferredSites)}`,
    `Widget and card churn:\n${lines(sample.widgets)}`,
  ].join("\n");
}

/** Resolves after the page paints `count` more frames. */
export function frames(page: Page, count: number): Promise<void> {
  return page.evaluate(async (remaining) => {
    for (let frame = 0; frame < remaining; frame++)
      await new Promise((resolve) => requestAnimationFrame(resolve));
  }, count);
}

/**
 * Resolves once every debounce an edit arms has fired and painted, twice over for chains. The first wait runs past
 * the longest autosave debounce, so the host's echo of the saved diff normally renders in the same step; a late
 * echo can only lower the count.
 */
export function settle(page: Page): Promise<void> {
  return page.evaluate(async () => {
    for (const wait of [800, 250]) {
      // Timers fire in deadline order, so this one runs after every shorter debounce already armed.
      await new Promise((resolve) => setTimeout(resolve, wait));
      for (let frame = 0; frame < 2; frame++)
        await new Promise((resolve) => requestAnimationFrame(resolve));
    }
  });
}
