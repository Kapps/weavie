import { createRoot, createSignal } from "solid-js";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DeferredFocus } from "../chrome/deferred-focus";
import { registerFloatingPanel } from "../chrome/floating-panels";
import { createToolPanels } from "./tool-panels";
import type { LayoutNode } from "./types";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));
vi.mock("../commands/context", () => ({ setContext: vi.fn() }));
const changeTool = vi.hoisted(() => vi.fn());
vi.mock("./store", () => ({ changeTool }));

const cleanups: (() => void)[] = [];
afterEach(() => {
  for (const dispose of cleanups.splice(0)) dispose();
  changeTool.mockReset();
});

function fixture(docked: boolean) {
  return createRoot((dispose) => {
    cleanups.push(dispose);
    const input = new EventTarget();
    const [backendId, setBackendId] = createSignal("a");
    const focus = new DeferredFocus(backendId, { request: vi.fn(), cancel: vi.fn() }, input);
    cleanups.push(() => focus.dispose());
    const [root, setRoot] = createSignal<LayoutNode>({
      type: "pane",
      id: "pane",
      kind: docked ? "files" : "editor",
    });
    const restoreFocus = vi.fn();
    const panels = createToolPanels({
      backendId,
      root,
      compact: () => false,
      revealDock: vi.fn(),
      restoreFocus,
      captureFocus: () => focus.capture(backendId()),
    });
    return { panels, input, setBackendId, setRoot, restoreFocus };
  });
}

describe("tool panel focus ownership", () => {
  it.each([
    "open",
    "toggleDock",
  ] as const)("a delayed %s cannot replace newer keyboard focus", async (action) => {
    const { panels, input } = fixture(true);
    const pending = Promise.withResolvers<void>();
    changeTool.mockReturnValueOnce(pending.promise);
    const operation = panels[action]("files");
    input.dispatchEvent(new Event("keydown"));
    pending.resolve();
    await operation;
    const focus = vi.fn();
    panels.focusRequest()!.intent.complete(focus);
    expect(focus).not.toHaveBeenCalled();
    expect(panels.visible("files")).toBe(true);
  });

  it("a delayed close reflects the layout result without stealing focus", async () => {
    const { panels, input, setRoot, restoreFocus } = fixture(true);
    const pending = Promise.withResolvers<void>();
    changeTool.mockReturnValueOnce(pending.promise);
    const operation = panels.close("files");
    input.dispatchEvent(new Event("focusin"));
    setRoot({ type: "pane", id: "pane", kind: "files", hidden: true });
    pending.resolve();
    await operation;
    expect(panels.visible("files")).toBe(false);
    expect(restoreFocus).not.toHaveBeenCalled();
  });

  it("consumes a current request once even across repeated visibility effects", async () => {
    const { panels } = fixture(false);
    await panels.open("files");
    const focus = vi.fn();
    panels.focusRequest()!.intent.complete(focus);
    panels.focusRequest()!.intent.complete(focus);
    expect(focus).toHaveBeenCalledOnce();
  });

  it("invalidates a published request before its eventual DOM focus", async () => {
    const { panels, input } = fixture(false);
    await panels.open("files");
    input.dispatchEvent(new Event("pointerdown"));
    const focus = vi.fn();
    panels.focusRequest()!.intent.complete(focus);
    expect(focus).not.toHaveBeenCalled();
  });

  it("captures after the dismissed popover restores its prior focus", async () => {
    const { panels, input } = fixture(false);
    const popover = registerFloatingPanel(
      "test-popover",
      () => {
        input.dispatchEvent(new Event("focusin"));
        popover.dispose();
      },
      "popover",
    );
    cleanups.push(() => popover.dispose());
    await panels.open("files");
    const focus = vi.fn();
    panels.focusRequest()!.intent.complete(focus);
    expect(focus).toHaveBeenCalledOnce();
  });

  it("does not publish an old backend's pending focus in the selected backend", async () => {
    const { panels, setBackendId } = fixture(true);
    const pending = Promise.withResolvers<void>();
    changeTool.mockReturnValueOnce(pending.promise);
    const operation = panels.open("files");
    setBackendId("b");
    pending.resolve();
    await operation;
    expect(panels.focusRequest()).toBeNull();
  });
});
