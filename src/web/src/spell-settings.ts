// Live spell-check settings. Core owns validation and injects the initial value before navigation; this module
// keeps the dynamically loaded Monaco surface in sync with later settings/MCP changes.

import { hostInjected, onHostMessage, type SpellSettings } from "./bridge";

export type { SpellSettings };

declare global {
  interface Window {
    /** Resolved spelling settings injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_SPELL_SETTINGS__?: SpellSettings;
  }
}

const DEFAULT_SETTINGS: SpellSettings = { enabled: true, locale: "en-US" };

let current = hostInjected(
  "__WEAVIE_SPELL_SETTINGS__",
  window.__WEAVIE_SPELL_SETTINGS__,
  DEFAULT_SETTINGS,
);

const subscribers = new Set<(settings: SpellSettings) => void>();

/** The spelling settings currently resolved by Core. */
export function currentSpellSettings(): SpellSettings {
  return current;
}

/** Subscribe to live spelling-setting changes. */
export function onSpellSettingsChanged(handler: (settings: SpellSettings) => void): () => void {
  subscribers.add(handler);
  return () => subscribers.delete(handler);
}

onHostMessage((message) => {
  if (message.type === "spell-settings") {
    current = { enabled: message.enabled, locale: message.locale };
    for (const handler of subscribers) {
      handler(current);
    }
  }
});
