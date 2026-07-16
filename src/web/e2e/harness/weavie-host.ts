import { type ChildProcess, spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, realpath, rm, writeFile } from "node:fs/promises";
import { Agent, get as httpGet } from "node:http";
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
  /** The isolated HOME the host runs under (WEAVIE_ROOT lives at `<home>/.weavie`). */
  readonly home: string;
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
  // A canned Notion doc; when set, the host's source connector is stubbed (WEAVIE_FAKE_NOTION) so a
  // notion.so/notion.site open-target fetches + renders it deterministically (see the fixtures option).
  notionDoc?: {
    title: string;
    markdown: string;
    editedTime?: string;
    truncated?: boolean;
    rejectEdits?: boolean;
  };
}

// Terminate the spawned host/runner AND every descendant (worker, claude, shell, LSP), resolving only once the
// whole tree is actually gone — so the workspace/HOME can be removed without a live process racing the delete.
// On Windows that race is the teardown killer: a surviving descendant whose cwd sits inside a worktree blocks
// `rm`, and fs.rm's retry backoff compounds with the tree's directory depth into a 10-60s stall that outlasts the
// test timeout (the "teardown hang" flake). Node's kill() reaches only the root there; `taskkill /T` kills its
// descendants, and the root's `close` event proves Windows has closed its process pipes before cleanup begins.
// POSIX asks the root for a graceful
// SIGINT (it forwards to its own children), escalating to SIGKILL if that stalls.
export function killProcessTree(proc: ChildProcess): Promise<void> {
  const pid = proc.pid;
  if (pid === undefined) {
    return Promise.resolve();
  }
  if (process.platform === "win32") {
    if (proc.exitCode !== null || proc.signalCode !== null) {
      return Promise.reject(
        new Error(`process tree root ${pid} exited before Windows tree shutdown`),
      );
    }
    return new Promise((resolve, reject) => {
      let settled = false;
      const settle = (action: () => void): void => {
        if (!settled) {
          settled = true;
          action();
        }
      };
      // `close` (the root's stdio pipes all shut) is the only reliable proof the tree is gone — taskkill's
      // own exit code is not: `/T` returns non-zero (e.g. 255) merely because a descendant self-exited before
      // it got there, while the root is dying and `close` is a beat behind.
      proc.once("close", () => settle(resolve));
      const taskkill = spawn("taskkill", ["/pid", String(pid), "/T", "/F"], { stdio: "ignore" });
      taskkill.once("exit", (code) => {
        // The kill attempt is done; `close` follows within a beat if the tree died. Bound the wait so a
        // genuinely surviving tree fails teardown loudly instead of hanging it forever.
        setTimeout(() => {
          settle(() =>
            reject(new Error(`process tree ${pid} survived taskkill (exit ${code ?? -1})`)),
          );
        }, 5000).unref();
      });
      taskkill.once("error", (error) => settle(() => reject(error)));
    });
  }
  if (proc.exitCode !== null || proc.signalCode !== null) {
    return Promise.resolve();
  }
  return new Promise((resolve) => {
    let settled = false;
    const finish = (): void => {
      if (settled) {
        return;
      }
      settled = true;
      resolve();
    };
    proc.once("exit", finish);
    proc.kill("SIGINT");
    // If `exit` hasn't fired in time (a child ignoring SIGINT, or a stalled dispose), force-kill and resolve so
    // teardown is bounded — a no-op once the process already exited.
    setTimeout(() => {
      if (!settled) {
        try {
          proc.kill("SIGKILL");
        } catch {
          // already gone
        }
        finish();
      }
    }, 3000).unref();
  });
}

// Resolve with the port the host actually bound (it prints the matched line only once its listener is up),
// so the browser never races the listener and parallel workers can never collide on a pre-picked port.
export function waitForPortLine(
  proc: ChildProcess,
  getLog: () => string,
  pattern: RegExp,
  timeoutMs: number,
): Promise<number> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(
      () => reject(new Error(`host did not report listening in time:\n${getLog()}`)),
      timeoutMs,
    );
    const onData = () => {
      const match = getLog().match(pattern);
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

// A drained GET over a caller-owned keep-alive agent: draining returns the socket to the pool so the next
// poll reuses it instead of opening a fresh loopback connection that leaks into Windows TIME_WAIT (#206).
export function getOverAgent(url: string, agent: Agent): Promise<{ status: number; body: string }> {
  return new Promise((resolve, reject) => {
    const req = httpGet(url, { agent }, (res) => {
      let body = "";
      res.setEncoding("utf8");
      res.on("data", (chunk) => {
        body += chunk;
      });
      res.on("end", () => resolve({ status: res.statusCode ?? 0, body }));
      res.on("error", reject);
    });
    req.on("error", reject);
  });
}

// Polls the host URL until it answers (any HTTP status), so callers connect only once the listener accepts.
export async function waitForHttp(
  url: string,
  getLog: () => string,
  timeoutMs: number,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  const agent = new Agent({ keepAlive: true, maxSockets: 1 });
  try {
    for (;;) {
      try {
        await getOverAgent(url, agent);
        return;
      } catch {
        if (Date.now() > deadline) {
          throw new Error(`host never answered ${url}:\n${getLog()}`);
        }
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
    }
  } finally {
    agent.destroy();
  }
}

// Shared per-test scaffolding both transports need: an isolated HOME, a throwaway git workspace, Claude
// stubbed at the process seam (resume off so no managed-session startup watcher fires on the fake), Codex
// forced onto its deterministic unavailable-session path, and the optional fake script + its readable log.
export interface FakeScaffold {
  home: string;
  workspace: string;
  env: NodeJS.ProcessEnv;
  fakeLog: () => string;
  cleanup: () => Promise<void>;
}

export async function prepareFake(options: LaunchOptions): Promise<FakeScaffold> {
  // Resolve the real path: on macOS os.tmpdir() is under the /var → /private/var symlink, and worktrees live
  // under WEAVIE_ROOT (=home/.weavie), so an unresolved home would desync a forked session's cwd (which the
  // kernel resolves) from the host's stored path. Idempotent on Linux/Windows.
  const home = await realpath(await mkdtemp(join(tmpdir(), "weavie-e2e-home-")));
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
    // Never launch the runner's real Codex. The missing binary becomes UnavailableStructuredAgentSession,
    // retaining provider identity, routing, and the structured pane without a model or network dependency.
    WEAVIE_CODEX_PATH: join(home, "missing-codex"),
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
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    ...fake.env,
    // Port 0: the OS assigns a free port at bind, so parallel workers can never race each other for one.
    WEAVIE_SERVE_PORT: "0",
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

  // The host prints the ready line only after its listener is bound and accepting, so the parsed port is
  // connectable the moment it appears.
  const port = await waitForPortLine(proc, () => log, /open\s+http:\/\/127\.0\.0\.1:(\d+)/, 40_000);
  const token = log.match(/open\s+http:\/\/127\.0\.0\.1:\d+\/index\.html\?token=([^\s]+)/)?.[1];
  if (token === undefined) {
    throw new Error(`headless host did not advertise its token-gated page:\n${log}`);
  }
  const url = `http://127.0.0.1:${port}/index.html?token=${token}`;

  return {
    url,
    workspace: fake.workspace,
    home: fake.home,
    log: () => log,
    fakeLog: fake.fakeLog,
    async stop() {
      await killProcessTree(proc);
      await fake.cleanup();
    },
  };
}
