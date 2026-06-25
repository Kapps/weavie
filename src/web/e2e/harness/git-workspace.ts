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
  // The host tree is already dead (killProcessTree awaited its exit), but Windows releases a dead process's file
  // handles asynchronously, so an immediate rm can still race them with EBUSY. A short bounded wait covers that OS
  // latency (mirrors Core's WorktreeManager) — NOT the old fallback that retried while real orphans stayed alive.
  await rm(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
}

// The absolute paths of every git worktree of `repoRoot` except the primary checkout — i.e. the worktrees
// HostCore created for forked sessions. Lets a multi-session test assert each session's writes land in its
// own worktree, with no dependence on Weavie's internal worktree-naming.
export function sessionWorktrees(repoRoot: string): string[] {
  const out = execFileSync("git", ["worktree", "list", "--porcelain"], {
    cwd: repoRoot,
    encoding: "utf8",
  });
  const paths = [...out.matchAll(/^worktree (.+)$/gm)].map((m) => m[1].trim());
  const primary = paths[0]; // git lists the main working tree first
  return paths.filter((p) => p !== primary);
}
