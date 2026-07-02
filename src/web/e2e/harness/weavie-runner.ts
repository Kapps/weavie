import { type ChildProcess, spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  type LaunchOptions,
  type WeavieHost,
  freePort,
  headlessDll,
  killProcessTree,
  prepareFake,
  waitForHttp,
} from "./weavie-host";

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..");
export const runnerDll = join(
  repoRoot,
  "src",
  "Weavie.Runner",
  "bin",
  "Debug",
  "net10.0",
  "Weavie.Runner.dll",
);

export function runnerBuilt(): boolean {
  return existsSync(runnerDll);
}

// Asks the runner control plane for its worker and returns the worker's page URL (carrying the worker
// token), once the backend reports running. The browser connects straight to the worker — the runner is out
// of the data path.
async function resolveWorkerUrl(
  control: string,
  token: string,
  getLog: () => string,
): Promise<string> {
  const deadline = Date.now() + 40_000;
  for (;;) {
    try {
      const res = await fetch(`${control}/backend?token=${token}`);
      if (res.ok) {
        const body = (await res.json()) as { url?: string; status?: string };
        if (body.url && body.status !== "failed") {
          return body.url;
        }
      }
    } catch {
      // control plane not up yet
    }
    if (Date.now() > deadline) {
      throw new Error(`runner never returned a worker backend:\n${getLog()}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 200));
  }
}

// Boots Weavie.Runner (the remote control plane), which spawns a Weavie.Headless worker over the same
// scaffold (HOME, fake claude, workspace inherited via env). The browser connects to the worker through the
// runner-issued URL+token — exercising the remote transport. The worker runs locally, so a @cross test's
// on-disk assertions still see the same workspace dir.
export async function launchRemote(options: LaunchOptions): Promise<WeavieHost> {
  const fake = await prepareFake(options);
  const runnerPort = await freePort();
  const runnerToken = randomBytes(16).toString("hex");
  const control = `http://127.0.0.1:${runnerPort}`;

  let log = "";
  const proc: ChildProcess = spawn(
    "dotnet",
    [
      runnerDll,
      "--workspace",
      fake.workspace,
      "--headless",
      headlessDll,
      "--port",
      String(runnerPort),
      "--bind",
      "127.0.0.1",
      "--token",
      runnerToken,
    ],
    { env: { ...process.env, ...fake.env }, stdio: ["ignore", "pipe", "pipe"] },
  );
  const collect = (chunk: Buffer) => {
    log += chunk.toString("utf8");
  };
  proc.stdout?.on("data", collect);
  proc.stderr?.on("data", collect);

  await waitForHttp(`${control}/backend?token=${runnerToken}`, () => log, 40_000);
  const url = await resolveWorkerUrl(control, runnerToken, () => log);
  await waitForHttp(url, () => log, 30_000);

  return {
    url,
    workspace: fake.workspace,
    home: fake.home,
    log: () => log,
    fakeLog: fake.fakeLog,
    async stop() {
      // Kill the runner AND its spawned worker (+ that worker's claude/shell/LSP children) — Node's kill()
      // reaches only the runner on Windows, orphaning the worker, whose live handles then block workspace removal.
      await killProcessTree(proc);
      await fake.cleanup();
    },
  };
}
