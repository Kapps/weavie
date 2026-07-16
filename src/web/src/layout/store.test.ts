import { createComputed, createRoot, createSignal } from "solid-js";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { LayoutDocument } from "./types";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

type HostMessage = { type: string; document?: LayoutDocument };
const posted = vi.hoisted(
  () => [] as Array<{ backendId: string; message: Record<string, unknown> }>,
);
const sessionHandlers = vi.hoisted(
  () => [] as Array<(message: HostMessage, backendId: string) => void>,
);
const bridgeState = vi.hoisted(
  () =>
    ({ setActiveBackendId: (_backendId: string): void => {} }) as {
      setActiveBackendId: (backendId: string) => void;
    },
);

vi.mock("../bridge", () => {
  const [activeBackendId, setActiveBackendId] = createSignal("local");
  bridgeState.setActiveBackendId = setActiveBackendId;
  return {
    activeBackendId,
    onSessionMessage: (handler: (message: HostMessage, backendId: string) => void) => {
      sessionHandlers.push(handler);
      return () => {};
    },
    postToBackend: (backendId: string, message: Record<string, unknown>) =>
      posted.push({ backendId, message }),
  };
});

const store = await import("./store");

const document = (top: number): LayoutDocument => ({
  version: 1,
  seenPaneLevel: 1,
  focused: "p_claude",
  dismissed: [],
  root: {
    type: "split",
    dir: "column",
    weights: [top, 1 - top],
    children: [
      { type: "pane", id: "p_claude", kind: "terminal:claude" },
      { type: "pane", id: "p_shell", kind: "terminal:shell" },
    ],
  },
});

beforeEach(() => {
  posted.length = 0;
  bridgeState.setActiveBackendId("local");
});

describe("layout host ownership", () => {
  it("restores the cached document when its backend becomes active", () => {
    const local = document(0.75);
    const remote = document(0.25);
    for (const handler of sessionHandlers) {
      handler({ type: "set-layout", document: remote }, "remote:test");
      handler({ type: "set-layout", document: local }, "local");
    }

    expect(store.layoutDocument()).toEqual(local);
    bridgeState.setActiveBackendId("remote:test");
    expect(store.layoutDocument()).toEqual(remote);
  });

  it("does not notify the active layout when a background backend restores", () => {
    const local = document(0.7);
    for (const handler of sessionHandlers) {
      handler({ type: "set-layout", document: local }, "local");
    }
    let notifications = 0;
    const dispose = createRoot((rootDispose) => {
      createComputed(() => {
        store.layoutDocument();
        notifications += 1;
      });
      return rootDispose;
    });

    for (const handler of sessionHandlers) {
      handler({ type: "set-layout", document: document(0.2) }, "remote:test");
    }

    expect(notifications).toBe(1);
    dispose();
  });

  it("sends layout changes to the backend that owned the gesture", () => {
    const changed = document(0.6);
    store.sendLayout("remote:test", changed);

    expect(posted).toEqual([
      {
        backendId: "remote:test",
        message: { type: "layout-changed", document: changed },
      },
    ]);
  });
});
