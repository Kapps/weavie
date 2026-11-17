// A repo's test profile on the web side: its test.profile, parsed for the session it is asked for. Drives the
// run-lens provider.

import { type ClientSession, log } from "../bridge";
import { onRepoStateChanged, repoStateOf } from "../repo-state";

/** One profile rule (mirrors Core's TestRule): a file glob + a symbol regex, plus the run-command templates. */
export interface TestRule {
  glob: string;
  symbol: string;
  runOne: string;
  runFile?: string;
  nameSeparator: string;
  header?: string;
}

const parsed = new Map<string, TestRule[]>();

/** A session's test rules (empty when unconfigured or its repo declared no tests). */
export function testRulesFor(session: ClientSession | null): TestRule[] {
  const json = repoStateOf(session).testProfile;
  let rules = parsed.get(json);
  if (rules === undefined) {
    rules = parseProfile(json);
    parsed.set(json, rules);
  }
  return rules;
}

/** Subscribes to profile changes (a host push). Returns an unsubscribe. Use outside a Solid reactive root. */
export const onTestProfileChanged = onRepoStateChanged;

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
      (entry?.runFile === undefined || entry.runFile === null || typeof entry.runFile === "string")
    ) {
      result.push({
        glob: entry.glob,
        symbol: entry.symbol,
        runOne: entry.runOne,
        runFile: typeof entry.runFile === "string" ? entry.runFile : undefined,
        nameSeparator: typeof entry.nameSeparator === "string" ? entry.nameSeparator : " ",
        header: typeof entry.header === "string" ? entry.header : undefined,
      });
    }
  }
  return result;
}
