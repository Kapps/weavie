// Agent settings are injected before navigation and re-pushed live. The New Session prompt reads
// them per backend so a remote host's providers and defaults never leak into another host.

import { createSignal } from "solid-js";
import {
  type AgentDefaults,
  hostConnection,
  hostInjected,
  LOCAL_BACKEND_ID,
  registerHostFeature,
} from "../bridge";

declare global {
  interface Window {
    /** Resolved agent settings injected by the local host before navigation. */
    __WEAVIE_AGENT__?: AgentDefaults;
  }
}

const DEFAULT: AgentDefaults = {
  defaultProvider: "claude",
  middleClickAutoscroll: true,
  providers: [
    {
      id: "claude",
      name: "Claude Code",
      available: true,
      unavailableReason: null,
      surface: "terminal",
    },
  ],
};

const injected = hostInjected("__WEAVIE_AGENT__", window.__WEAVIE_AGENT__, DEFAULT);
const [byBackend, setByBackend] = createSignal(
  new Map<string, AgentDefaults>([[LOCAL_BACKEND_ID, injected]]),
);

function defaultsFor(backendId: string): AgentDefaults {
  return (
    byBackend().get(backendId) ?? {
      defaultProvider: "",
      middleClickAutoscroll: true,
      providers: [],
    }
  );
}

/** Whether Linux middle-click autoscroll is enabled for the structured agent transcript. */
export function agentMiddleClickAutoscrollEnabled(): boolean {
  return defaultsFor(LOCAL_BACKEND_ID).middleClickAutoscroll;
}

/** The provider the New Session prompt should preselect. */
export function defaultAgentProvider(backendId: string): string {
  return defaultsFor(backendId).defaultProvider;
}

/** Provider profiles advertised by one exact host. */
export function agentProviders(backendId: string): AgentDefaults["providers"] {
  return defaultsFor(backendId).providers;
}

/** Remember the provider just chosen as that host's default. */
export function setDefaultAgentProvider(backendId: string, providerId: string): void {
  setByBackend((previous) => {
    const next = new Map(previous);
    const current = next.get(backendId);
    if (current !== undefined) {
      next.set(backendId, { ...current, defaultProvider: providerId });
    }
    return next;
  });
  hostConnection(backendId)?.host.feature("agentDefaults").publish("setProvider", { providerId });
}

registerHostFeature((connection) => {
  const update = (defaults: AgentDefaults): void => {
    setByBackend((previous) => new Map(previous).set(connection.id, defaults));
  };
  const offHello = connection.onHello((hello) => update(hello.agentDefaults));
  const offSettings = connection.host
    .feature("settings")
    .on<AgentDefaults>("agent-defaults", update);
  return () => {
    offHello();
    offSettings();
    if (!connection.isLocal) {
      setByBackend((previous) => {
        const next = new Map(previous);
        next.delete(connection.id);
        return next;
      });
    }
  };
});
