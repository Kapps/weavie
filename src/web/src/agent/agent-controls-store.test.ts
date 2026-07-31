import { describe, expect, it, vi } from "vitest";
import type { AgentControlState, AgentModelChoice, ClientSession } from "../bridge";

interface MockSession {
  connection: { id: string };
  address: { slot: string; incarnation: string };
  feature: (feature: string) => {
    on: (name: string, listener: (payload: Record<string, unknown>) => void) => () => void;
    publish: (name: string, payload: Record<string, unknown>) => void;
  };
}

const bridge = vi.hoisted(() => ({
  installer: undefined as ((session: MockSession) => undefined | (() => void)) | undefined,
  listeners: new Map<string, (payload: Record<string, unknown>) => void>(),
  posted: [] as Array<{
    backendId: string;
    slot: string;
    feature: string;
    name: string;
    payload: Record<string, unknown>;
  }>,
}));

function session(backendId: string, slot: string): MockSession {
  return {
    connection: { id: backendId },
    address: { slot, incarnation: `${slot}-incarnation` },
    feature: (feature: string) => ({
      on: (name: string, listener: (payload: Record<string, unknown>) => void) => {
        bridge.listeners.set(`${backendId}\0${slot}\0${feature}\0${name}`, listener);
        return () => {};
      },
      publish: (name: string, payload: Record<string, unknown>) => {
        bridge.posted.push({ backendId, slot, feature, name, payload });
      },
    }),
  };
}

const sessions = new Map<string, MockSession>();
function sessionForSlot(backendId: string, slot: string) {
  const key = `${backendId}\0${slot}`;
  let value = sessions.get(key);
  if (value === undefined) {
    value = session(backendId, slot);
    sessions.set(key, value);
    bridge.installer?.(value);
  }
  return value;
}

const owner = (backendId: string, slot: string): ClientSession =>
  sessionForSlot(backendId, slot) as unknown as ClientSession;

function emitControls(backendId: string, slot: string, value: AgentControlState): void {
  sessionForSlot(backendId, slot);
  bridge.listeners.get(`${backendId}\0${slot}\0agent\0controls`)?.({ state: value });
}

vi.mock("../bridge", () => ({
  registerSessionFeature: (installer: (value: MockSession) => undefined | (() => void)) => {
    bridge.installer = installer;
    return () => {};
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

const planState: AgentControlState = {
  ...state,
  axes: [
    {
      id: "collaborationMode",
      label: "Mode",
      value: "default",
      valueLabel: "Default",
      options: [
        { id: "plan", label: "Plan", description: null },
        { id: "default", label: "Default", description: null },
      ],
      commandId: "weavie.agent.togglePlanMode",
    },
  ],
};

describe("agent controls store", () => {
  it("records host-pushed control state per slot and stays empty for others", () => {
    emitControls("remote-a", "slot-a", state);

    expect(store.agentControlState(owner("remote-a", "slot-a"))).toEqual(state);
    expect(store.agentControlState(owner("remote-a", "slot-b")).modelControl.models).toEqual([]);
    expect(store.currentModel(owner("remote-a", "slot-a"))?.id).toBe("gpt-5.5");
    expect(store.currentModel(owner("remote-a", "slot-b"))).toBeUndefined();
  });

  it("posts a live control change to the session's backend", () => {
    bridge.posted.length = 0;
    store.setAgentControl(owner("remote-a", "slot-a"), "model", "gpt-5.4-mini");

    expect(bridge.posted).toEqual([
      {
        backendId: "remote-a",
        slot: "slot-a",
        feature: "agent",
        name: "setControl",
        payload: { axis: "model", value: "gpt-5.4-mini" },
      },
    ]);
  });

  it("toggles the command-owned mode between advertised Plan and default presets", () => {
    bridge.posted.length = 0;
    emitControls("remote-a", "slot-plan", planState);

    expect(
      store.toggleAgentControl(owner("remote-a", "slot-plan"), "weavie.agent.togglePlanMode"),
    ).toBe(true);
    expect(bridge.posted.at(-1)?.payload).toMatchObject({
      axis: "collaborationMode",
      value: "plan",
    });

    emitControls("remote-a", "slot-plan", {
      ...planState,
      axes: planState.axes.map((axis) => ({ ...axis, value: "plan", valueLabel: "Plan" })),
    });
    store.toggleAgentControl(owner("remote-a", "slot-plan"), "weavie.agent.togglePlanMode");
    expect(bridge.posted.at(-1)?.payload).toMatchObject({ value: "default" });
  });

  it("selecting an effort under a non-current model switches model first, then sets effort", () => {
    bridge.posted.length = 0;
    store.selectModelEffort(owner("remote-a", "slot-a"), mini, "low");

    expect(bridge.posted.map((entry) => [entry.payload.axis, entry.payload.value])).toEqual([
      ["model", "gpt-5.4-mini"],
      ["effort", "low"],
    ]);
  });

  it("selecting an effort under the current model sets only the effort", () => {
    bridge.posted.length = 0;
    store.selectModelEffort(owner("remote-a", "slot-a"), gpt55, "low");

    expect(bridge.posted.map((entry) => [entry.payload.axis, entry.payload.value])).toEqual([
      ["effort", "low"],
    ]);
  });

  it("toggling Fast sends the model's fast tier when off and standard when on", () => {
    bridge.posted.length = 0;
    store.toggleModelFast(owner("remote-a", "slot-a"), gpt55); // off -> priority
    store.toggleModelFast(owner("remote-a", "slot-a"), { ...gpt55, fastOn: true }); // on -> standard

    expect(bridge.posted.map((entry) => entry.payload.value)).toEqual(["priority", "standard"]);
  });

  it("toggling Fast on a model without a fast tier does nothing", () => {
    bridge.posted.length = 0;
    store.toggleModelFast(owner("remote-a", "slot-a"), mini);
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
