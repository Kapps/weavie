// The active workspace's test profile on the web side: parsed from window.__WEAVIE_TEST_PROFILE__ (injected
// before navigation) and re-parsed on each { type: "test-profile" } push. Drives the run-lens provider.

import { createSignal } from "solid-js";
import { log, onHostMessage } from "../bridge";

/** One profile rule (mirrors Core's TestRule): a file glob + a symbol regex, plus the run-command templates. */
export interface TestRule {
  glob: string;
  symbol: string;
  runOne: string;
  runFile: string;
  nameSeparator: string;
  header?: string;
}

declare global {
  interface Window {
    /** The raw test.profile JSON the host injects before navigation ("" when unconfigured). */
    __WEAVIE_TEST_PROFILE__?: string;
  }
}

const [rules, setRules] = createSignal<TestRule[]>(
  parseProfile(window.__WEAVIE_TEST_PROFILE__ ?? ""),
);
const changeListeners = new Set<() => void>();

/** The active workspace's test rules (empty when unconfigured or the repo declared no tests). */
export const testRules = rules;

/** Subscribes to profile changes (a host push). Returns an unsubscribe. Use outside a Solid reactive root. */
export function onTestProfileChanged(listener: () => void): () => void {
  changeListeners.add(listener);
  return () => {
    changeListeners.delete(listener);
  };
}

/** Parses the raw test.profile JSON into rules; a bad or empty value yields no rules (no lenses). */
export function parseProfile(json: string): TestRule[] {
  if (json.trim() === "") {
    return [];
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch (error) {
    log("warn", `test-profile: ignoring invalid JSON (${String(error)})`);
    return [];
  }
  if (!Array.isArray(parsed)) {
    return [];
  }
  const result: TestRule[] = [];
  for (const entry of parsed) {
    if (
      typeof entry?.glob === "string" &&
      typeof entry?.symbol === "string" &&
      typeof entry?.runOne === "string" &&
      typeof entry?.runFile === "string"
    ) {
      result.push({
        glob: entry.glob,
        symbol: entry.symbol,
        runOne: entry.runOne,
        runFile: entry.runFile,
        nameSeparator: typeof entry.nameSeparator === "string" ? entry.nameSeparator : " ",
        header: typeof entry.header === "string" ? entry.header : undefined,
      });
    }
  }
  return result;
}

onHostMessage((message) => {
  if (message.type === "test-profile") {
    setRules(parseProfile(message.profile));
    for (const listener of changeListeners) {
      listener();
    }
  }
});
