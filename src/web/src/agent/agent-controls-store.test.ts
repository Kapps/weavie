import { describe, expect, it, vi } from "vitest";
import type { AgentControlState, AgentModelChoice } from "../bridge";

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

const gpt55: AgentModelChoice = {
  id: "gpt-5.5",
  label: "GPT-5.5",
  current: true,
  effort: "medium",
  efforts: [
    { id: "low", label: "Low", description: null },
    { id: "medium", label: "Medium", description: null },
  ],
  fastTier: "priority",
  fastOn: false,
};
const mini: AgentModelChoice = {
  id: "gpt-5.4-mini",
  label: "GPT-5.4 mini",
  current: false,
  effort: "low",
  efforts: [{ id: "low", label: "Low", description: null }],
  fastTier: "",
  fastOn: false,
};

const state: AgentControlState = {
  modelControl: { value: "gpt-5.5", valueLabel: "GPT-5.5 (Medium)", models: [gpt55, mini] },
  axes: [],
  slash: [],
};

describe("agent controls store", () => {
  it("records host-pushed control state per slot and stays empty for others", () => {
    bridge.listener?.({ type: "agent-controls", slot: "slot-a", workspace: "/w", state });

    expect(store.agentControlState("slot-a")).toEqual(state);
    expect(store.agentControlState("slot-b").modelControl.models).toEqual([]);
    expect(store.currentModel("slot-a")?.id).toBe("gpt-5.5");
    expect(store.currentModel("slot-b")).toBeUndefined();
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

  it("selecting an effort under a non-current model switches model first, then sets effort", () => {
    bridge.posted.length = 0;
    store.selectModelEffort("remote-a", "slot-a", mini, "low");

    expect(bridge.posted.map((entry) => [entry.message.axis, entry.message.value])).toEqual([
      ["model", "gpt-5.4-mini"],
      ["effort", "low"],
    ]);
  });

  it("selecting an effort under the current model sets only the effort", () => {
    bridge.posted.length = 0;
    store.selectModelEffort("remote-a", "slot-a", gpt55, "low");

    expect(bridge.posted.map((entry) => [entry.message.axis, entry.message.value])).toEqual([
      ["effort", "low"],
    ]);
  });

  it("toggling Fast sends the model's fast tier when off and standard when on", () => {
    bridge.posted.length = 0;
    store.toggleModelFast("remote-a", "slot-a", gpt55); // off -> priority
    store.toggleModelFast("remote-a", "slot-a", { ...gpt55, fastOn: true }); // on -> standard

    expect(bridge.posted.map((entry) => entry.message.value)).toEqual(["priority", "standard"]);
  });

  it("toggling Fast on a model without a fast tier does nothing", () => {
    bridge.posted.length = 0;
    store.toggleModelFast("remote-a", "slot-a", mini);
    expect(bridge.posted).toEqual([]);
  });

  it("tracks which axis picker is open", () => {
    expect(store.openControlAxis()).toBeNull();
    store.openControlPicker("model");
    expect(store.openControlAxis()).toBe("model");
    store.closeControlPicker();
    expect(store.openControlAxis()).toBeNull();
  });
});
