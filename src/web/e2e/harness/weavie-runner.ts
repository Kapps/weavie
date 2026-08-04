import { type ChildProcess, spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { Agent } from "node:http";
import { headlessProgram, programExists, runnerProgram } from "./test-programs";
import {
  getOverAgent,
  killProcessTree,
  type LaunchOptions,
  prepareFake,
  type WeavieHost,
  waitForHttp,
  waitForPortLine,
} from "./weavie-host";

export function runnerBuilt(): boolean {
  return programExists(runnerProgram);
}

export async function launchRunner(options: LaunchOptions): Promise<WeavieHost> {
  const fake = await prepareFake(options);
  const runnerToken = randomBytes(16).toString("hex");

  let log = "";
  const proc: ChildProcess = spawn(
    runnerProgram.command,
    [
      ...runnerProgram.args,
      "--workspace",
      fake.workspace,
      "--headless",
      headlessProgram.target,
      "--port",
      "0",
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

  let runnerPort: number;
  try {
    // Port 0 makes the listener allocation race-free; the ready line reports the chosen port.
    runnerPort = await waitForPortLine(
      proc,
      () => log,
      /control plane:\s+http:\/\/127\.0\.0\.1:(\d+)/,
      12_000,
    );
  } catch (error) {
    await killProcessTree(proc);
    await fake.cleanup();
    throw error;
  }

  return {
    url: `http://127.0.0.1:${runnerPort}`,
    token: runnerToken,
    workspace: fake.workspace,
    home: fake.home,
    log: () => log,
    fakeLog: fake.fakeLog,
    async stop() {
      // The runner owns the worker and its agent/shell/LSP descendants, so teardown must kill the whole tree.
      await killProcessTree(proc);
      await fake.cleanup();
    },
  };
}

// Asks the runner control plane for its worker's clean page URL and separate connect token. The browser
// connects straight to the worker — the runner is out of the data path.
async function resolveWorker(
  control: string,
  token: string,
  getLog: () => string,
  deadline: number,
): Promise<{ url: string; token: string }> {
  // One keep-alive socket for the whole poll (see getOverAgent): the control plane is already up, so each
  // 200ms probe would otherwise be a fresh TIME_WAIT connection.
  const agent = new Agent({ keepAlive: true, maxSockets: 1 });
  try {
    for (;;) {
      try {
        const res = await getOverAgent(`${control}/backend`, agent, {
          Authorization: `Bearer ${token}`,
        });
        if (res.status >= 200 && res.status < 300) {
          const body = JSON.parse(res.body) as { url?: string; token?: string; status?: string };
          if (body.url && body.token && body.status === "running") {
            return { url: body.url, token: body.token };
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
  } finally {
    agent.destroy();
  }
}

// Boots Weavie.Runner (the remote control plane), which spawns a Weavie.Headless worker over the same
// scaffold (HOME, fake claude, workspace inherited via env). The browser connects to the worker through the
// runner-issued URL+token — exercising the remote transport. The worker runs locally, so a @cross test's
// on-disk assertions still see the same workspace dir.
export async function launchRemote(options: LaunchOptions): Promise<WeavieHost> {
  const runner = await launchRunner(options);
  // One budget for the rest of the boot, sized to fit inside Playwright's 30s test timeout: a stalled
  // worker must fail HERE, with the runner log in the error, not as an opaque fixture timeout without it.
  const bootDeadline = Date.now() + 14_000;
  let worker: { url: string; token: string };
  try {
    worker = await resolveWorker(runner.url, runner.token, runner.log, bootDeadline);
    await waitForHttp(worker.url, runner.log, bootDeadline - Date.now());
  } catch (error) {
    await runner.stop();
    throw error;
  }

  return {
    ...runner,
    url: worker.url,
    token: worker.token,
  };
}
