// The agent.defaultProvider setting, host-owned: injected as window.__WEAVIE_AGENT__ before navigation and
// re-pushed as `settings.agent-defaults` on change — a local-machine push (like notification prefs). The
// New Session prompt reads it to preselect a provider; creating a session with a different one writes it back.

import {
  type AgentDefaults,
  hostConnection,
  hostInjected,
  LOCAL_BACKEND_ID,
  registerHostFeature,
} from "../bridge";

declare global {
  interface Window {
    /** Resolved agent defaults injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_AGENT__?: AgentDefaults;
  }
}

// Plain-browser dev fallback (no host injection); mirrors the host's default provider.
const DEFAULT: AgentDefaults = { defaultProvider: "claude" };

let current: AgentDefaults = hostInjected("__WEAVIE_AGENT__", window.__WEAVIE_AGENT__, DEFAULT);

/** The provider the New Session prompt should preselect — read when it opens, so it tracks the setting. */
export function defaultAgentProvider(): "claude" | "codex" {
  return current.defaultProvider;
}

/** Remember the provider just chosen as the default, so the next prompt preselects it. Persists to the local host. */
export function setDefaultAgentProvider(providerId: "claude" | "codex"): void {
  current = { defaultProvider: providerId };
  hostConnection(LOCAL_BACKEND_ID)
    ?.host.feature("agentDefaults")
    .publish("setProvider", { providerId });
}

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }
  return connection.host.feature("settings").on<AgentDefaults>("agent-defaults", (defaults) => {
    current = defaults;
  });
});
