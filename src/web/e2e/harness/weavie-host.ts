import { type ChildProcess, spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { type FakeStep, writeFakeClaudeWrapper, writeFakeScript } from "./fake-claude";
import { createGitWorkspace, createPrWorkspace, removeWorkspace } from "./git-workspace";

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..");
export const headlessDll = join(
  repoRoot,
  "src",
  "Weavie.Headless",
  "bin",
  "Debug",
  "net10.0",
  "Weavie.Headless.dll",
);

export function headlessBuilt(): boolean {
  return existsSync(headlessDll);
}

export interface WeavieHost {
  readonly url: string;
  readonly workspace: string;
  /** Everything the host has written to stdout so far (status lines). */
  log(): string;
  /** The fake claude's markers (its MCP/hook activity), or "" when no script ran. */
  fakeLog(): string;
  stop(): Promise<void>;
}

export interface LaunchOptions {
  fakeScript: FakeStep[] | null;
  // When true, the workspace is a PR scenario (base + head branches off a local "origin") and the host's PR
  // provider is stubbed (WEAVIE_FAKE_PRS) with the canned PR pointing at the head branch — the Open-PR journey.
  pr?: boolean;
  // A canned Notion doc ({ title, markdown, editedTime? }); when set, the host's source connector is stubbed
  // (WEAVIE_FAKE_NOTION) so a notion.so/notion.site open-target fetches + renders it deterministically.
  notionDoc?: { title: string; markdown: string; editedTime?: string };
}

// Terminate the spawned host/runner (Windows: AND its descendants — worker, claude, shell, LSP), then resolve
// once it has actually exited, so the workspace/HOME can be removed without racing live handles. Node's kill()
// reaches only the root process on Windows; taskkill /T kills the whole tree. Resolving on the real `exit` is the
// deterministic signal (not a fixed sleep), but a graceful shutdown that stalls (a child that ignores SIGINT, or
// a slow dispose) must not hang teardown — so escalate to an unconditional kill after a bounded grace.
export function killProcessTree(proc: ChildProcess): Promise<void> {
  return new Promise((resolve) => {
    const pid = proc.pid;
    if (proc.exitCode !== null || proc.signalCode !== null || pid === undefined) {
      resolve();
      return;
    }
    let settled = false;
    const finish = (): void => {
      if (settled) {
        return;
      }
      settled = true;
      resolve();
    };
    const forceKill = (): void => {
      if (process.platform === "win32") {
        spawn("taskkill", ["/pid", String(pid), "/T", "/F"], { stdio: "ignore" });
      } else {
        try {
          proc.kill("SIGKILL");
        } catch {
          // already gone
        }
      }
    };
    proc.once("exit", finish);
    // Windows: force-kill the tree immediately (taskkill is the only thing that reaches descendants). POSIX: ask
    // for a graceful SIGINT first.
    if (process.platform === "win32") {
      forceKill();
    } else {
      proc.kill("SIGINT");
    }
    // If `exit` hasn't fired in time (a child ignoring SIGINT, or a stalled dispose), force-kill and resolve so
    // teardown is bounded — a no-op once the process already exited.
    setTimeout(() => {
      if (settled) {
        return;
      }
      forceKill();
      finish();
    }, 3000).unref();
  });
}

export function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const srv = createServer();
    srv.listen(0, "127.0.0.1", () => {
      const address = srv.address();
      if (address === null || typeof address === "string") {
        reject(new Error("could not allocate a port"));
        return;
      }
      const { port } = address;
      srv.close(() => resolve(port));
    });
  });
}

// Resolve with the port the host actually bound, parsed from its ready line, so the browser never races
// the listener.
function waitForListening(
  proc: ChildProcess,
  getLog: () => string,
  timeoutMs: number,
): Promise<number> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(
      () => reject(new Error(`host did not report listening in time:\n${getLog()}`)),
      timeoutMs,
    );
    const onData = () => {
      const match = getLog().match(/open\s+http:\/\/127\.0\.0\.1:(\d+)/);
      if (match) {
        clearTimeout(timer);
        proc.stdout?.off("data", onData);
        resolve(Number(match[1]));
      }
    };
    proc.stdout?.on("data", onData);
    proc.on("exit", (code) => {
      clearTimeout(timer);
      reject(new Error(`host exited early with code ${code}:\n${getLog()}`));
    });
  });
}

// Polls the host URL until it answers (any HTTP status), so callers connect only once the listener accepts.
export async function waitForHttp(
  url: string,
  getLog: () => string,
  timeoutMs: number,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      await fetch(url, { redirect: "manual" });
      return;
    } catch {
      if (Date.now() > deadline) {
        throw new Error(`host never answered ${url}:\n${getLog()}`);
      }
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
  }
}

// Shared per-test scaffolding both transports need: an isolated HOME, a throwaway git workspace, claude
// stubbed at the process seam (resume off so no managed-session startup watcher fires on the fake), and the
// optional fake script + its readable debug log. The worker inherits these env vars.
export interface FakeScaffold {
  home: string;
  workspace: string;
  env: NodeJS.ProcessEnv;
  fakeLog: () => string;
  cleanup: () => Promise<void>;
}

export async function prepareFake(options: LaunchOptions): Promise<FakeScaffold> {
  const home = await mkdtemp(join(tmpdir(), "weavie-e2e-home-"));
  const pr = options.pr ? await createPrWorkspace() : null;
  const workspace = pr?.dir ?? (await createGitWorkspace());
  const wrapper = await writeFakeClaudeWrapper(home);
  const fakeLogPath = join(home, "fake-claude.log");
  const env: NodeJS.ProcessEnv = {
    HOME: home,
    // Isolate Weavie's and Claude's on-disk config away from the developer's real home. $HOME alone doesn't do
    // this on Windows (the user-profile known folder ignores it), so pin the two roots explicitly: WEAVIE_ROOT
    // for ~/.weavie (settings, worktrees) and CLAUDE_CONFIG_DIR for ~/.claude (the IDE lock). Without it a run
    // reads real settings (e.g. claude.allowAllTools) and writes real config — non-deterministic and polluting.
    WEAVIE_ROOT: join(home, ".weavie"),
    CLAUDE_CONFIG_DIR: join(home, ".claude"),
    WEAVIE_CLAUDE_PATH: wrapper,
    WEAVIE_CLAUDE_RESUMESESSION: "false",
  };
  if (options.fakeScript) {
    env.WEAVIE_FAKE_CLAUDE_SCRIPT = await writeFakeScript(home, options.fakeScript);
    env.WEAVIE_FAKE_CLAUDE_LOG = fakeLogPath;
  }
  if (pr) {
    const prsPath = join(home, "fake-prs.json");
    await writeFile(prsPath, JSON.stringify({ prs: pr.prs, comments: pr.comments }));
    env.WEAVIE_FAKE_PRS = prsPath;
  }
  if (options.notionDoc) {
    const notionPath = join(home, "fake-notion.json");
    await writeFile(notionPath, JSON.stringify(options.notionDoc));
    env.WEAVIE_FAKE_NOTION = notionPath;
  }
  return {
    home,
    workspace,
    env,
    fakeLog: () => (existsSync(fakeLogPath) ? readFileSync(fakeLogPath, "utf8") : ""),
    cleanup: () =>
      // Same bounded wait as removeWorkspace: the tree is dead, but Windows frees its handles under HOME (logs,
      // config, IDE lock) asynchronously, so an immediate rm can race them.
      Promise.all([
        rm(home, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }),
        removeWorkspace(workspace),
      ]).then(),
  };
}

// Boots a real Weavie.Headless over the scaffold (browser → WSS → Weavie.Headless). Returns once the host
// reports — and actually accepts on — the port it bound.
export async function launchHeadless(options: LaunchOptions): Promise<WeavieHost> {
  const fake = await prepareFake(options);
  const requestedPort = await freePort();
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    ...fake.env,
    WEAVIE_SERVE_PORT: String(requestedPort),
    WEAVIE_SERVE_WORKSPACE: fake.workspace,
  };

  let log = "";
  const proc = spawn("dotnet", [headlessDll], { env, stdio: ["ignore", "pipe", "pipe"] });
  proc.stdout?.on("data", (chunk: Buffer) => {
    log += chunk.toString("utf8");
  });
  proc.stderr?.on("data", (chunk: Buffer) => {
    log += chunk.toString("utf8");
  });

  const port = await waitForListening(proc, () => log, 40_000);
  const url = `http://127.0.0.1:${port}/`;
  // The "open …" line can print just before the listener actually accepts (a fresh HOME does first-run
  // setup in between), so poll the socket until it responds rather than racing the browser into a refused
  // connection.
  await waitForHttp(url, () => log, 30_000);

  return {
    url,
    workspace: fake.workspace,
    log: () => log,
    fakeLog: fake.fakeLog,
    async stop() {
      await killProcessTree(proc);
      await fake.cleanup();
    },
  };
}
