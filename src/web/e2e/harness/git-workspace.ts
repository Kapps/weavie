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

// One canned PR for the Open-PR harness, matching a branch in the PR workspace below. Mirrors the
// WEAVIE_FAKE_PRS JSON the headless host reads (FakePullRequests.FromFile).
export interface PrSeed {
  number: number;
  title: string;
  author: string;
  headRef: string;
  baseRef: string;
  url: string;
  draft: boolean;
}

// A git workspace wired for the Open-PR flow: a local bare repo stands in for `origin` (reached via an
// insteadOf rewrite of a github.com URL, so `git remote get-url` still parses to owner/repo while every fetch
// stays offline), a base branch (main) and a head branch with a real multi-file diff. The head branch is
// pushed then deleted locally, so opening the PR exercises the real fetch-into-a-local-branch path. Returns the
// working tree plus the canned PR that points at the head branch.
// One canned review comment for the Open-PR harness, anchored to a line of the head-branch diff.
export interface CommentSeed {
  id: number;
  path: string;
  line: number;
  side: "left" | "right";
  author: string;
  body: string;
  createdAt: string;
  inReplyTo: number;
}

export async function createPrWorkspace(): Promise<{
  dir: string;
  prs: PrSeed[];
  comments: CommentSeed[];
}> {
  // No dot in the bare dir name: it becomes a git config subsection (url.<path>.insteadOf), where a dot would
  // be misparsed as a key separator.
  const bare = await mkdtemp(join(tmpdir(), "weavie-e2e-origin"));
  const dir = await mkdtemp(join(tmpdir(), "weavie-e2e-ws-"));
  const originUrl = "https://github.com/acme/demo.git";
  const headRef = "pr-branch";
  const g = (cwd: string, ...args: string[]) => execFileSync("git", args, { cwd, stdio: "ignore" });

  g(bare, "init", "-q", "--bare");
  for (const [name, content] of Object.entries(SEED)) {
    await writeFile(join(dir, name), content);
  }
  g(dir, "init", "-q", "-b", "main");
  g(dir, "config", "user.email", "e2e@example.com");
  g(dir, "config", "user.name", "Weavie E2E");
  // origin's stored URL is a github.com one (so RepoRef parses owner/repo), rewritten to the local bare repo for
  // every transport — no network, fully deterministic.
  g(dir, "remote", "add", "origin", originUrl);
  g(dir, "config", `url.${bare}.insteadOf`, originUrl);
  g(dir, "add", "-A");
  g(dir, "commit", "-q", "-m", "seed");
  g(dir, "push", "-q", "-u", "origin", "main");

  // The PR's head branch: a modified file (a two-hunk diff) plus a new file — a multi-file changeset to walk.
  g(dir, "checkout", "-q", "-b", headRef);
  await writeFile(
    join(dir, "hello.ts"),
    "export function greet(name: string): string {\n" +
      "  return `Hi there, ${name}!`;\n" +
      "}\n\n" +
      'const message = greet("pull request");\n' +
      "console.log(message);\n",
  );
  await writeFile(join(dir, "feature.ts"), "export const feature = true;\n");
  g(dir, "add", "-A");
  g(dir, "commit", "-q", "-m", "pr changes");
  g(dir, "push", "-q", "origin", headRef);
  // Back on base, head branch gone locally — opening the PR must fetch it fresh (the real path).
  g(dir, "checkout", "-q", "main");
  g(dir, "branch", "-q", "-D", headRef);

  return {
    dir,
    prs: [
      {
        number: 101,
        title: "Add a feature and tweak the greeting",
        author: "alice",
        headRef,
        baseRef: "main",
        url: "https://github.com/acme/demo/pull/101",
        draft: false,
      },
    ],
    // A review comment on the changed greeting line of hello.ts (right/head side).
    comments: [
      {
        id: 1,
        path: "hello.ts",
        line: 2,
        side: "right",
        author: "bob",
        body: "Why change this greeting?",
        createdAt: "2026-01-01T00:00:00Z",
        inReplyTo: 0,
      },
    ],
  };
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
