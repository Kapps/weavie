import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export interface TestProgram {
  readonly command: string;
  readonly args: string[];
  readonly target: string;
}

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..");
const bundleRoot = process.env.WEAVIE_E2E_BUNDLE;
const executableSuffix = process.platform === "win32" ? ".exe" : "";

function resolveProgram(
  name: string,
  localDirectory: string[],
  bundleDirectory: string[],
): TestProgram {
  const target =
    bundleRoot === undefined
      ? join(repoRoot, ...localDirectory, `${name}.dll`)
      : join(bundleRoot, ...bundleDirectory, `${name}${executableSuffix}`);
  return bundleRoot === undefined
    ? { command: "dotnet", args: [target], target }
    : { command: target, args: [], target };
}

export const headlessProgram = resolveProgram(
  "Weavie.Headless",
  ["src", "Weavie.Headless", "bin", "Debug", "net10.0"],
  ["runner", "worker"],
);

export const runnerProgram = resolveProgram(
  "Weavie.Runner",
  ["src", "Weavie.Runner", "bin", "Debug", "net10.0"],
  ["runner"],
);

export const fakeClaudeProgram = resolveProgram(
  "Weavie.FakeClaude",
  ["tools", "Weavie.FakeClaude", "bin", "Debug", "net10.0"],
  ["fake-claude"],
);

export function programExists(program: TestProgram): boolean {
  return existsSync(program.target);
}
