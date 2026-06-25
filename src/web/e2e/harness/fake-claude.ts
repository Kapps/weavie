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
export type FakeStep =
  | { op: "print"; text: string }
  | { op: "sleep"; ms: number }
  | { op: "edit"; path: string; content: string }
  | { op: "hook"; request: Record<string, unknown> }
  | { op: "mcp"; tool: string; args?: Record<string, unknown>; server?: "ide" };

export async function writeFakeScript(dir: string, steps: FakeStep[]): Promise<string> {
  const path = join(dir, "fake-claude-script.json");
  await writeFile(path, JSON.stringify(steps));
  return path;
}

// One Claude edit as the change tracker sees it: PreToolUse snapshots the file's baseline, the file is
// written, PostToolUse records the new content. This 3-beat is what the tracker folds into the post-turn
// review set (the `edit` op alone never registers — the tracker is hook-driven). `path` may use the
// {{WORKSPACE}} placeholder, which the fake resolves to the session's worktree.
export function appliedEdit(path: string, content: string): FakeStep[] {
  const toolInput = { file_path: path };
  const hookFor = (event: string): FakeStep => ({
    op: "hook",
    request: {
      hook_event_name: event,
      tool_name: "Edit",
      tool_input: toolInput,
      cwd: "{{WORKSPACE}}",
    },
  });
  return [hookFor("PreToolUse"), { op: "edit", path, content }, hookFor("PostToolUse")];
}

// Ends the turn (a Stop hook → Idle), which arms and auto-opens the post-turn review of the applied edits —
// the same surfacing a real turn produces. Append after one or more appliedEdit() sequences.
export function endTurn(): FakeStep {
  return { op: "hook", request: { hook_event_name: "Stop" } };
}
