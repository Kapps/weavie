import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { ClientSession, SearchMatch } from "../bridge";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

const view = vi.hoisted(() => ({
  shown: null as unknown,
  installers: [] as ((session: unknown) => undefined | (() => void))[],
  show: (_session: unknown): void => {},
}));
vi.mock("../bridge", () => {
  let leave = (): void => {};
  let present =
    (_session: unknown): (() => void) =>
    () => {};
  view.show = (session) => {
    leave();
    view.shown = session;
    leave = present(session);
  };
  return {
    registerSessionFeature: (installer: (session: unknown) => undefined | (() => void)) => {
      view.installers.push(installer);
      return () => {};
    },
    registerViewFeature: (installer: (session: unknown) => () => void) => {
      present = installer;
      return () => {};
    },
    requireOnScreen: (session: unknown) => {
      if (session !== view.shown) throw new Error("That session is no longer on screen.");
    },
  };
});
const committed = vi.hoisted(() => vi.fn());
vi.mock("./search-prefs", async () => {
  const solid: typeof import("solid-js") = await import(["solid-js", "dist/solid.js"].join("/"));
  const [options] = solid.createSignal({
    caseSensitive: false,
    wholeWord: false,
    regex: false,
    excludeGitignored: true,
    include: "",
    exclude: "",
  });
  const [terms] = solid.createSignal<string[]>([]);
  return {
    searchOptions: options,
    recentTerms: terms,
    commitSearchTerm: committed,
    updateSearchOptions: () => {},
    updateSearchOptionsDebounced: () => {},
  };
});

// The store's timers are the page's; Node's globals stand in, faked per test.
vi.stubGlobal("window", globalThis);
const store = await import("./search-store");

const match = (path: string): SearchMatch => ({ path, line: 1, column: 1, preview: "needle" });
function session(paths: string[]): ClientSession {
  const client = {
    closed: false,
    feature: () => ({ request: () => Promise.resolve({ matches: paths.map(match) }) }),
  } as unknown as ClientSession;
  for (const install of view.installers) install(client);
  return client;
}

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

it("refuses to step a session's results once another session's are shown", async () => {
  const a = session(["/a/one.ts", "/a/two.ts"]);
  const b = session(["/b/one.ts", "/b/two.ts"]);
  const opened = vi.fn();
  store.setSearchOpener(opened);
  view.show(a);
  store.setQuery("needle");
  await vi.runAllTimersAsync();
  view.show(b);
  await vi.runAllTimersAsync();
  expect(store.searchState.phase()).toBe("ready");
  expect(store.searchState.matches().map((hit) => hit.path)).toEqual(["/b/one.ts", "/b/two.ts"]);

  expect(() => store.stepSearchResult(a, 1)).toThrow("That session is no longer on screen.");
  expect(store.searchState.selected()).toBe(0);
  expect(opened).not.toHaveBeenCalled();
  expect(committed).not.toHaveBeenCalled();

  expect(store.stepSearchResult(b, 1)).toBe(true);
  expect(opened).toHaveBeenCalledExactlyOnceWith(b, match("/b/two.ts"), true);
});

it("declines with no session to step", () => {
  expect(store.stepSearchResult(null, 1)).toBe(false);
});
