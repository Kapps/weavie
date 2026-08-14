// Agent settings are injected before navigation and re-pushed live. The
// New Session prompt reads it to preselect a provider; creating a session with a different one writes it back.

import {
  type AgentSettingsSpec,
  hostConnection,
  hostInjected,
  LOCAL_BACKEND_ID,
  registerHostFeature,
} from "../bridge";

declare global {
  interface Window {
    /** Resolved agent settings injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_AGENT_SETTINGS__?: AgentSettingsSpec;
  }
}

// Plain-browser dev fallback (no host injection); mirrors the host's default provider.
const DEFAULT: AgentSettingsSpec = { defaultProvider: "claude", middleClickAutoscroll: true };

let current = hostInjected("__WEAVIE_AGENT_SETTINGS__", window.__WEAVIE_AGENT_SETTINGS__, DEFAULT);

/** The provider the New Session prompt should preselect — read when it opens, so it tracks the setting. */
export function defaultAgentProvider(): "claude" | "codex" {
  return current.defaultProvider;
}

/** Whether Linux middle-click autoscroll is enabled for the structured agent transcript. */
export function agentMiddleClickAutoscrollEnabled(): boolean {
  return current.middleClickAutoscroll;
}

/** Remember the provider just chosen as the default, so the next prompt preselects it. Persists to the local host. */
export function setDefaultAgentProvider(providerId: "claude" | "codex"): void {
  current = { ...current, defaultProvider: providerId };
  hostConnection(LOCAL_BACKEND_ID)
    ?.host.feature("agentDefaults")
    .publish("setProvider", { providerId });
}

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }
  return connection.host.feature("settings").on<AgentSettingsSpec>("agent-settings", (settings) => {
    current = settings;
  });
});
