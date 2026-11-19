import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { afterAll, expect, it } from "vitest";

// The selection lint rules (lint/*.grit) run by Biome over fixtures written inside src/, where the rules apply.
const web = join(import.meta.dirname, "..");
const biome = join(web, "node_modules", "@biomejs", "biome", "bin", "biome");
const fixtures = mkdtempSync(join(web, "src", "lint-fixture-"));
afterAll(() => rmSync(fixtures, { recursive: true, force: true }));

// The name each selection rule reports for `source`, one per diagnostic.
function flagged(source: string): string[] {
  const file = join(fixtures, "probe.ts");
  writeFileSync(file, source);
  let report: string;
  try {
    report = execFileSync(process.execPath, [biome, "lint", "--reporter=github", file], {
      cwd: web,
      encoding: "utf8",
    });
  } catch (error) {
    report = (error as { stdout: string }).stdout;
  }
  return report
    .split("\n")
    .filter((line) => line.startsWith("::error title=plugin,"))
    .map((line) => line.slice(line.lastIndexOf("::") + 2).split(" ")[0] ?? "");
}

it("reports a named alias at its import", () => {
  expect(
    flagged(`import { selectedSession as shown } from "../bridge";
export const read = shown();
`),
  ).toEqual(["selectedSession"]);
});

it("reports literal computed access", () => {
  expect(
    flagged(`import * as bridge from "../bridge";
export const read = bridge["selectedSession"];
export const listen = bridge[\`onSelectedSession\`];
export const backend = bridge['activeBackendId'];
`),
  ).toEqual(["selectedSession", "onSelectedSession", "activeBackendId"]);
});

it("reports a re-export, renamed or not", () => {
  expect(
    flagged(`export { selectedSession as shown } from "../bridge";
export { onSelectedSession } from "../bridge";
export { activeBackendId as backend } from "../bridge";
`),
  ).toEqual(["selectedSession", "onSelectedSession", "activeBackendId"]);
});

it("passes the sanctioned names and look-alike strings", () => {
  expect(
    flagged(`import { backendIdOf, isSelected, registerViewFeature } from "../bridge";
export const names = [backendIdOf, isSelected, registerViewFeature, "selectedSessionDeletable"];
`),
  ).toEqual([]);
});
