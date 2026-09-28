import { createRoot, createSignal } from "solid-js";
import { afterEach, describe, expect, it, onTestFinished, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { activeTabFor, captureViewState, closeTabFor, openTabFor } from "./session-store";
import type { EditorSession } from "./session-types";
import { createTabActivity } from "./tab-activity";

const bridge = vi.hoisted(() => ({
  installer: undefined as ((session: ClientSession) => (() => void) | undefined) | undefined,
  selected: (): ClientSession | null => null,
  reads: 0,
}));
vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));
vi.mock("../bridge", () => ({
  registerSessionFeature: (installer: typeof bridge.installer) => {
    bridge.installer = installer;
    return () => {};
  },
  selectedSession: () => {
    bridge.reads++;
    return bridge.selected();
  },
}));

afterEach(() => vi.useRealTimers());

function fixture() {
  vi.useFakeTimers();
  return createRoot((dispose) => {
    onTestFinished(dispose);
    const [selected, select] = createSignal<ClientSession | null>(null);
    bridge.selected = selected;
    const session = () => {
      const lifetime = new AbortController();
      let receive!: (value: EditorSession) => void;
      const client = {
        signal: lifetime.signal,
        connection: {
          reportError: (error: unknown) => {
            throw error;
          },
        },
        state: {
          editor: {
            subscribe(listener: typeof receive) {
              receive = listener;
              return () => {};
            },
          },
        },
        feature: () => ({ publish: () => {} }),
      } as unknown as ClientSession;
      const teardown = bridge.installer!(client)!;
      onTestFinished(teardown);
      const restore = (active: string) =>
        receive({
          active,
          open: [
            { path: "/a.ts", viewState: null },
            { path: "/b.ts", viewState: null },
          ],
          review: null,
        });
      restore("/a.ts");
      return { client, lifetime, teardown, restore };
    };
    const a = session();
    const b = session();
    select(a.client);
    const tab = activeTabFor(a.client)!;
    const active = createTabActivity(tab);
    return { a, b, tab, active, select };
  });
}

describe("owned tab activity", () => {
  it("tracks session and exact active-tab selection without recomputing on scroll capture", () => {
    const f = fixture();
    expect(f.active()).toBe(true);
    const reads = bridge.reads;
    for (let top = 0; top < 3000; top++) {
      captureViewState(f.tab, { top });
      expect(f.active()).toBe(true);
    }
    expect(bridge.reads).toBe(reads);
    openTabFor(f.a.client, "/b.ts", {});
    expect(f.active()).toBe(false);
    openTabFor(f.a.client, "/a.ts", {});
    expect(f.active()).toBe(true);
    f.select(f.b.client);
    expect(f.active()).toBe(false);
    f.select(f.a.client);
    expect(f.active()).toBe(true);
  });

  it("is false inside tab teardown before selection or map deletion can notify", () => {
    const f = fixture();
    const during = vi.fn(() => f.active());
    f.tab.signal.addEventListener("abort", during, { once: true });
    const reads = bridge.reads;
    f.a.teardown();
    expect(during).toHaveReturnedWith(false);
    expect(bridge.reads).toBe(reads);
    expect(bridge.selected()).toBe(f.a.client);
    expect(activeTabFor(f.a.client)).toBeUndefined();
    expect(f.active()).toBe(false);
  });

  it("is false inside session abort even while the tab and selection still exist", () => {
    const f = fixture();
    const during = vi.fn(() => f.active());
    f.a.lifetime.signal.addEventListener("abort", during, { once: true });
    f.a.lifetime.abort();
    expect(during).toHaveReturnedWith(false);
    expect(f.tab.signal.aborted).toBe(false);
    expect(activeTabFor(f.a.client)).toBe(f.tab);
    expect(f.active()).toBe(false);
  });

  it("cannot reactivate a retired owner when the same path reopens", () => {
    const f = fixture();
    closeTabFor(f.a.client, "/a.ts");
    expect(f.active()).toBe(false);
    openTabFor(f.a.client, "/a.ts", {});
    const reopened = activeTabFor(f.a.client)!;
    expect(reopened).not.toBe(f.tab);
    expect(reopened.signal.aborted).toBe(false);
    expect(reopened.entry.path).toBe(f.tab.entry.path);
    expect(f.active()).toBe(false);
  });

  it("rejects the old owner during same-path tab-kind replacement", () => {
    const f = fixture();
    const during = vi.fn(() => f.active());
    f.tab.signal.addEventListener("abort", during, { once: true });
    openTabFor(f.a.client, "/a.ts", { kind: "source" });
    expect(during).toHaveReturnedWith(false);
    expect(activeTabFor(f.a.client)?.entry.kind).toBe("source");
    expect(f.active()).toBe(false);
  });
});
