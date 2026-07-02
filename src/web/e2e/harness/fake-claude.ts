import { existsSync } from "node:fs";
import { chmod, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// tools/Weavie.FakeClaude/bin/Debug/net10.0/Weavie.FakeClaude.dll, relative to this file
// (src/web/e2e/harness → repo root is four levels up).
const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..");
export const fakeClaudeDll = join(
  repoRoot,
  "tools",
  "Weavie.FakeClaude",
  "bin",
  "Debug",
  "net10.0",
  "Weavie.FakeClaude.dll",
);

export function fakeClaudeBuilt(): boolean {
  return existsSync(fakeClaudeDll);
}

// Weavie execs the claude.path setting as a single executable, so wrap the managed dll in a tiny launcher
// and point claude.path at that. Written into the test's isolated HOME. Windows can't CreateProcess a `.sh`,
// so write a `.cmd` there — WindowsPtyLauncher runs `.cmd`/`.bat` through cmd.exe; POSIX gets an exec'd shell script.
export async function writeFakeClaudeWrapper(dir: string): Promise<string> {
  if (process.platform === "win32") {
    const wrapper = join(dir, "fake-claude.cmd");
    await writeFile(wrapper, `@dotnet "${fakeClaudeDll}" %*\r\n`);
    return wrapper;
  }
  const wrapper = join(dir, "fake-claude.sh");
  await writeFile(wrapper, `#!/bin/sh\nexec dotnet ${JSON.stringify(fakeClaudeDll)} "$@"\n`);
  await chmod(wrapper, 0o755);
  return wrapper;
}

// A fake-claude script: an ordered list of steps the fake runs on launch (print/sleep/edit/hook/mcp).
// `waitFile` blocks until the test creates the named signal file, so a step can follow a user action
// deterministically (e.g. a turn-boundary hook after the test keeps a hunk).
export type FakeStep =
  | { op: "print"; text: string }
  | { op: "sleep"; ms: number }
  | { op: "edit"; path: string; content: string }
  | { op: "hook"; request: Record<string, unknown> }
  | { op: "waitFile"; path: string }
  | { op: "mcp"; tool: string; args?: Record<string, unknown>; server?: "ide" };

export async function writeFakeScript(dir: string, steps: FakeStep[]): Promise<string> {
  const path = join(dir, "fake-claude-script.json");
  await writeFile(path, JSON.stringify(steps));
  return path;
}
