import { chmod, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fakeClaudeProgram, programExists, type TestProgram } from "./test-programs";

export type FakeInference = "disabled" | "failure" | "needsDetail" | "success";

export function fakeClaudeBuilt(): boolean {
  return programExists(fakeClaudeProgram);
}

// Weavie execs the claude.path setting as one executable, so wrap the resolved local DLL or packaged apphost
// and point claude.path at it. WindowsPtyLauncher runs .cmd through cmd.exe; POSIX gets an exec'd shell script.
export async function writeFakeClaudeWrapper(dir: string): Promise<string> {
  const name = process.platform === "win32" ? "fake-claude.cmd" : "fake-claude.sh";
  return writeTestProgramWrapper(dir, name, fakeClaudeProgram, []);
}

export async function writeTestProgramWrapper(
  dir: string,
  name: string,
  program: TestProgram,
  extraArgs: string[],
): Promise<string> {
  const command = [program.command, ...program.args, ...extraArgs];
  const wrapper = join(dir, name);
  if (process.platform === "win32") {
    await writeFile(wrapper, `@${command.map((part) => `"${part}"`).join(" ")} %*\r\n`);
    return wrapper;
  }
  await writeFile(
    wrapper,
    `#!/bin/sh\nexec ${command.map((part) => JSON.stringify(part)).join(" ")} "$@"\n`,
  );
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
