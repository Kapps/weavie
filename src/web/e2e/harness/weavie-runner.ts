import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { LaunchOptions, WeavieHost } from "./weavie-host";

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..");
export const runnerDll = join(
  repoRoot,
  "src",
  "Weavie.Runner",
  "bin",
  "Debug",
  "net10.0",
  "Weavie.Runner.dll",
);

export function runnerBuilt(): boolean {
  return existsSync(runnerDll);
}

// Implemented in the remote-transport task (see docs/specs/integration-testing-strategy.md). Until then the
// `remote` project is skipped by the fixture's runnerBuilt() guard, so this is never reached in CI.
export function launchRemote(_options: LaunchOptions): Promise<WeavieHost> {
  return Promise.reject(new Error("launchRemote not implemented yet"));
}
