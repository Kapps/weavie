import { execFileSync } from "node:child_process";
import { mkdtemp, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

// Canonicalize a fresh temp dir. On macOS os.tmpdir() is under /var, a symlink to /private/var, and the
// kernel resolves a process's cwd — so the fake claude's Directory.GetCurrentDirectory() (which backs
// {{WORKSPACE}}) yields /private/var while the host holds the unresolved /var string, and change-tracking
// path checks (IsWithinWorkspace) miss. Resolving at the source makes every layer agree; idempotent on
// Linux/Windows (no symlink), so it leaves those unaffected.
async function makeTempDir(prefix: string): Promise<string> {
  return realpath(await mkdtemp(join(tmpdir(), prefix)));
}

// Valid 8×8 solid-color PNGs (signature + IHDR + zlib IDAT + IEND, CRC-correct), for the media-pane
// journeys: PIXEL_RED seeds pixel.png; PIXEL_BLUE overwrites it to drive the fs-change refresh.
export const PIXEL_RED = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEklEQVR4nGP4z8CAFWEXHbQSACj/P8Fu7N9hAAAAAElFTkSuQmCC",
  "base64",
);
export const PIXEL_BLUE = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEElEQVR4nGNgYPiPAw0pCQCpcD/BFMrqcwAAAABJRU5ErkJggg==",
  "base64",
);

/**
 * A small inline 200×80 PNG so seeded/injected images render with no network. It must be data:image/png
 * (or gif/jpeg/webp): markdown-it's validateLink rejects every other data: URI (data:image/svg+xml never
 * renders); DOMPurify then also allows data: on <img src>. Wide enough that the image's hover point (its
 * center) stays clear of the embed-zoom corner magnifier.
 */
export const ZOOM_IMAGE_SRC =
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAMgAAABQCAIAAADTD63nAAAAqElEQVR42u3SMQ0AAAgEsVeFOmQiiAELjE2q4HKpHngXCTAWxsJYYCyMhbHAWBgLY4GxMBbGAmNhLIwFxsJYGAuMhbEwFhgLY2EsMBbGwlhgLIyFscBYGAtjgbEwFsYCY2EsjAXGwlgYC4yFsTAWGAtjYSwwFsbCWGAsjIWxwFgYC2OBsTAWxgJjYSyMBcbCWBgLjIWxMBYYC2NhLDAWxsJYYCyMhbHgLARVPJITwkh1AAAAAElFTkSuQmCC";

// Seed files every journey can rely on: a markdown doc (preview), one with image + diagram embeds
// (embed-zoom — seeded rather than typed, so no spec races a multi-hundred-char data URI through Monaco),
// a TypeScript file (syntax highlight + edit/persist + LSP), a plain-text file, and media files (the
// image/video pane). Kept tiny and deterministic.
const SEED: Record<string, string | Buffer> = {
  "README.md":
    "# Sample project\n\nHello **world** — this is _markdown_.\n\n" +
    "```mermaid\ngraph TD\n  A[Start] --> B[End]\n```\n\n" +
    "```ts\nconst answer: number = 42;\n```\n",
  "zoom.md": `# Zoomables\n\n![block](${ZOOM_IMAGE_SRC})\n\n\`\`\`mermaid\ngraph LR\n  A[One] --> B[Two]\n\`\`\`\n`,
  "hello.ts":
    "export function greet(name: string): string {\n" +
    "  return `Hello, ${name}!`;\n" +
    "}\n\n" +
    'const message = greet("weavie");\n' +
    "console.log(message);\n",
  "notes.txt": "just plain text\n",
  "pixel.png": PIXEL_RED,
  // Not a decodable video — enough to drive the media pane's byte pipeline; decode is the browser's job.
  "clip.webm": Buffer.from("not-a-real-webm"),
};

// A throwaway git repo so HostCore can create sessions/worktrees off HEAD. Returns the path; call
// removeWorkspace when done.
export async function createGitWorkspace(): Promise<string> {
  const dir = await makeTempDir("weavie-e2e-ws-");
  for (const [name, content] of Object.entries(SEED)) {
    await writeFile(join(dir, name), content);
  }
  const git = (...args: string[]) => execFileSync("git", args, { cwd: dir, stdio: "ignore" });
  git("init", "-q", "-b", "main");
  git("config", "user.email", "e2e@example.com");
  git("config", "user.name", "Weavie E2E");
  // Keep line endings LF regardless of the runner's global git config (GitHub's Windows image defaults
  // core.autocrlf=true), so seeded/worktree content matches the LF the specs assert on across every OS.
  git("config", "core.autocrlf", "false");
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
  const bare = await makeTempDir("weavie-e2e-origin");
  const dir = await makeTempDir("weavie-e2e-ws-");
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
  g(dir, "config", "core.autocrlf", "false"); // LF regardless of the runner's global config (see createGitWorkspace)
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

  // A SECOND PR (#102) on its own head branch, with DISTINCT changed files (widget.ts added, notes.txt
  // modified) — never overlapping #101's files — so two PR sessions can be checked for cross-contamination.
  const headRef2 = "pr-branch-2";
  g(dir, "checkout", "-q", "-b", headRef2);
  await writeFile(join(dir, "widget.ts"), "export const widget = 42;\n");
  await writeFile(join(dir, "notes.txt"), "just plain text\nplus a second-PR line\n");
  g(dir, "add", "-A");
  g(dir, "commit", "-q", "-m", "pr 102 changes");
  g(dir, "push", "-q", "origin", headRef2);
  g(dir, "checkout", "-q", "main");
  g(dir, "branch", "-q", "-D", headRef2);

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
      {
        number: 102,
        title: "Add a widget and note",
        author: "carol",
        headRef: headRef2,
        baseRef: "main",
        url: "https://github.com/acme/demo/pull/102",
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
