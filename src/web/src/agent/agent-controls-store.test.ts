import { describe, expect, it, vi } from "vitest";
import type { AgentControlState } from "../bridge";

const bridge = vi.hoisted(() => ({
  listener: undefined as ((message: Record<string, unknown>) => void) | undefined,
  posted: [] as Array<{ backendId: string; message: Record<string, unknown> }>,
}));

vi.mock("../bridge", () => ({
  onHostMessage: (listener: (message: Record<string, unknown>) => void) => {
    bridge.listener = listener;
    return () => {};
  },
  postToBackend: (backendId: string, message: Record<string, unknown>) => {
    bridge.posted.push({ backendId, message });
  },
}));

const store = await import("./agent-controls-store");

const state: AgentControlState = {
  axes: [
    {
      id: "model",
      label: "Model",
      value: "gpt-5.5",
      valueLabel: "GPT-5.5",
      options: [{ id: "gpt-5.5", label: "GPT-5.5", description: null }],
    },
  ],
  slash: [],
};

describe("agent controls store", () => {
  it("records host-pushed control state per slot and stays empty for others", () => {
    bridge.listener?.({ type: "agent-controls", slot: "slot-a", workspace: "/w", state });

    expect(store.agentControlState("slot-a")).toEqual(state);
    expect(store.agentControlState("slot-b").axes).toEqual([]);
    expect(store.agentControlState(null).axes).toEqual([]);
  });

  it("posts a live control change to the session's backend", () => {
    bridge.posted.length = 0;
    store.setAgentControl("remote-a", "slot-a", "model", "gpt-5.4-mini");

    expect(bridge.posted).toEqual([
      {
        backendId: "remote-a",
        message: {
          type: "agent-set-control",
          slot: "slot-a",
          axis: "model",
          value: "gpt-5.4-mini",
        },
      },
    ]);
  });

  it("tracks which axis picker is open", () => {
    expect(store.openControlAxis()).toBeNull();
    store.openControlPicker("model");
    expect(store.openControlAxis()).toBe("model");
    store.closeControlPicker();
    expect(store.openControlAxis()).toBeNull();
  });
});
