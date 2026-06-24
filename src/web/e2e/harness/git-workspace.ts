import { execFileSync } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

// Seed files every journey can rely on: a markdown doc (preview), a TypeScript file (syntax highlight +
// edit/persist + LSP), and a plain-text file. Kept tiny and deterministic.
const SEED: Record<string, string> = {
  "README.md": "# Sample project\n\nHello **world** — this is _markdown_.\n",
  "hello.ts":
    "export function greet(name: string): string {\n" +
    "  return `Hello, ${name}!`;\n" +
    "}\n\n" +
    'const message = greet("weavie");\n' +
    "console.log(message);\n",
  "notes.txt": "just plain text\n",
};

// A throwaway git repo so HostCore can create sessions/worktrees off HEAD. Returns the path; call
// removeWorkspace when done.
export async function createGitWorkspace(): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "weavie-e2e-ws-"));
  for (const [name, content] of Object.entries(SEED)) {
    await writeFile(join(dir, name), content);
  }
  const git = (...args: string[]) => execFileSync("git", args, { cwd: dir, stdio: "ignore" });
  git("init", "-q", "-b", "main");
  git("config", "user.email", "e2e@example.com");
  git("config", "user.name", "Weavie E2E");
  git("add", "-A");
  git("commit", "-q", "-m", "seed");
  return dir;
}

export async function removeWorkspace(dir: string): Promise<void> {
  await rm(dir, { recursive: true, force: true });
}
