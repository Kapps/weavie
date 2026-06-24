import { type ChildProcess, spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, rm } from "node:fs/promises";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { type FakeStep, writeFakeClaudeWrapper, writeFakeScript } from "./fake-claude";
import { createGitWorkspace, removeWorkspace } from "./git-workspace";

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
  /** Everything the host has written to stdout so far (status lines, fake-claude markers). */
  log(): string;
  stop(): Promise<void>;
}

export interface LaunchOptions {
  fakeScript: FakeStep[] | null;
}

function freePort(): Promise<number> {
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
async function waitForHttp(url: string, getLog: () => string, timeoutMs: number): Promise<void> {
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

// Boots a real Weavie.Headless over a throwaway git workspace, with HOME redirected to a temp dir (so all
// ~/.weavie state is isolated per test) and the claude binary stubbed by the fake. Returns once the host
// reports the port it bound.
export async function launchHeadless(options: LaunchOptions): Promise<WeavieHost> {
  const home = await mkdtemp(join(tmpdir(), "weavie-e2e-home-"));
  const workspace = await createGitWorkspace();
  const wrapper = await writeFakeClaudeWrapper(home);

  const requestedPort = await freePort();
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    HOME: home,
    WEAVIE_SERVE_PORT: String(requestedPort),
    WEAVIE_SERVE_WORKSPACE: workspace,
    // Stub claude at the process seam; resume off so no managed-session startup watcher fires on the fake.
    WEAVIE_CLAUDE_PATH: wrapper,
    WEAVIE_CLAUDE_RESUMESESSION: "false",
  };
  if (options.fakeScript) {
    env.WEAVIE_FAKE_CLAUDE_SCRIPT = await writeFakeScript(home, options.fakeScript);
  }

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
    workspace,
    log: () => log,
    async stop() {
      proc.kill("SIGINT");
      await new Promise((resolve) => setTimeout(resolve, 200));
      await Promise.all([rm(home, { recursive: true, force: true }), removeWorkspace(workspace)]);
    },
  };
}
