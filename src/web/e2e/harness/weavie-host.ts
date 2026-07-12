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
// On Windows that race is the teardown killer: a surviving descendant whose cwd sits inside the workspace or a
// worktree blocks `rm` with EBUSY. A one-shot `taskkill /T` is not enough there: it walks a parent-pid snapshot,
// so it misses children spawned mid-kill (a just-reopened session still bringing up its shell/claude) and any
// child whose parent already exited. Instead, kill the descendant closure computed from the live process table —
// Win32_Process keeps a dead parent's pid in ParentProcessId, so orphans stay reachable — re-enumerating until
// the tree is empty, and fail LOUDLY with the survivors rather than letting a later rm hit EBUSY. POSIX asks the
// root for a graceful SIGINT (it forwards to its own children), escalating to SIGKILL if that stalls.
export function killProcessTree(proc: ChildProcess): Promise<void> {
  const pid = proc.pid;
  if (pid === undefined) {
    return Promise.resolve();
  }
  if (process.platform === "win32") {
    return killWindowsTree(proc, pid);
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

// One PowerShell run owns the whole bounded kill loop: each pass recomputes the root's descendant closure from
// a fresh Win32_Process table (accumulated across passes, so a link whose parent died between passes stays in
// the set), force-kills every member still alive, and exits 0 only once a pass finds none. Exit 1 prints the
// survivors as JSON.
function windowsTreeKillScript(rootPid: number): string {
  return `
$ErrorActionPreference = 'SilentlyContinue'
$known = [System.Collections.Generic.HashSet[int]]::new()
[void]$known.Add(${rootPid})
for ($attempt = 0; $attempt -lt 8; $attempt++) {
  $table = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name
  do {
    $grew = $false
    foreach ($row in $table) {
      if ($known.Contains([int]$row.ParentProcessId) -and -not $known.Contains([int]$row.ProcessId)) {
        [void]$known.Add([int]$row.ProcessId)
        $grew = $true
      }
    }
  } while ($grew)
  $alive = @($table | Where-Object { $known.Contains([int]$_.ProcessId) })
  if ($alive.Count -eq 0) { exit 0 }
  foreach ($row in $alive) { Stop-Process -Force -Id ([int]$row.ProcessId) }
  Start-Sleep -Milliseconds 150
}
$table = Get-CimInstance Win32_Process | Select-Object ProcessId, Name
$alive = @($table | Where-Object { $known.Contains([int]$_.ProcessId) })
if ($alive.Count -gt 0) { $alive | ConvertTo-Json -Compress; exit 1 }
exit 0
`;
}

async function killWindowsTree(proc: ChildProcess, pid: number): Promise<void> {
  // Attach before any await: if the root is alive now, its `close` (pipes released) can't slip past us.
  const rootClosed =
    proc.exitCode === null && proc.signalCode === null
      ? new Promise<void>((resolve) => proc.once("close", () => resolve()))
      : null;
  const survivors = await new Promise<string>((resolve, reject) => {
    let out = "";
    const shell = spawn(
      "powershell.exe",
      ["-NoProfile", "-NonInteractive", "-Command", windowsTreeKillScript(pid)],
      { stdio: ["ignore", "pipe", "ignore"] },
    );
    shell.stdout.on("data", (chunk: Buffer) => {
      out += chunk.toString("utf8");
    });
    shell.once("error", reject);
    shell.once("exit", (code) => resolve(code === 0 ? "" : out.trim() || `exit ${code}`));
  });
  if (survivors) {
    throw new Error(`Windows tree shutdown left processes alive: ${survivors}`);
  }
  if (rootClosed !== null) {
    await rootClosed;
  }
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
  const url = `http://127.0.0.1:${port}/`;

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
