// The agent.defaultProvider setting, host-owned: injected as window.__WEAVIE_AGENT__ before navigation and
// re-pushed as { type: "agent-defaults" } on change — a local-machine push (like notification prefs). The
// New Session prompt reads it to preselect a provider; creating a session with a different one writes it back.

import { type AgentDefaults, hostInjected, onHostMessage } from "../bridge";

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

onHostMessage((message) => {
  if (message.type === "agent-defaults") {
    const { type: _type, ...defaults } = message;
    current = defaults;
  }
});
