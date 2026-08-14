import { describe, expect, it, vi } from "vitest";
import type { AgentControlState, ClientSession } from "../bridge";

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
function sessionForSlot(backendId: string, slot: string): MockSession {
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

const state: AgentControlState = {
  axes: [
    {
      id: "model",
      label: "Model",
      description: "Model used for this session",
      category: "model",
      kind: "select",
      value: "gpt-5.5",
      valueLabel: "GPT-5.5",
      options: [
        { id: "gpt-5.5", label: "GPT-5.5", description: null, group: null },
        { id: "gpt-5.4", label: "GPT-5.4", description: null, group: null },
      ],
    },
  ],
  slash: [],
};

describe("agent controls store", () => {
  it("records host-pushed ACP controls per exact session", () => {
    emitControls("remote-a", "slot-a", state);

    expect(store.agentControlState(owner("remote-a", "slot-a"))).toEqual(state);
    expect(store.agentControlState(owner("remote-a", "slot-b"))).toEqual({ axes: [], slash: [] });
  });

  it("echoes an opaque control id and value to its owning session", () => {
    bridge.posted.length = 0;
    store.setAgentControl(owner("remote-a", "slot-a"), "model", "gpt-5.4");

    expect(bridge.posted).toEqual([
      {
        backendId: "remote-a",
        slot: "slot-a",
        feature: "agent",
        name: "setControl",
        payload: { axis: "model", value: "gpt-5.4" },
      },
    ]);
  });

  it("tracks which provider-owned axis picker is open", () => {
    expect(store.openControlAxis()).toBeNull();
    store.openControlPicker("model");
    expect(store.openControlAxis()).toBe("model");
    store.closeControlPicker();
    expect(store.openControlAxis()).toBeNull();
  });
});
