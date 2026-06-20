// Visual capture for REMOTE SESSIONS (docs/specs/remote-sessions.md): drives the actual runner flow —
// the picker page creates a session (a real git worktree + a spawned, token-gated Weavie.Headless worker),
// then the browser connects to that worker over the token-gated bridge and the real app boots remotely.
//
// Run from src/web after `pnpm run build` (so dist exists):  node e2e/capture-remote.mjs
// Builds Weavie.Headless (copies the fresh dist into wwwroot) and Weavie.Runner, records to e2e/.recordings/.

import { spawn } from "node:child_process";
import { mkdirSync } from "node:fs";
import { createServer } from "node:net";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "@playwright/test";

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(webRoot, "..", "..");
const headlessProject = join(repoRoot, "src", "Weavie.Headless", "Weavie.Headless.csproj");
const runnerProject = join(repoRoot, "src", "Weavie.Runner", "Weavie.Runner.csproj");
const headlessDll = join(repoRoot, "src", "Weavie.Headless", "bin", "Debug", "net10.0", "Weavie.Headless.dll");
const runnerDll = join(repoRoot, "src", "Weavie.Runner", "bin", "Debug", "net10.0", "Weavie.Runner.dll");
const outDir = join(webRoot, "e2e", ".recordings");
const viewport = { width: 1280, height: 800 };
const RUNNER_TOKEN = "capturetoken";

async function tour(page, runnerUrl) {
  const settle = (ms) => page.waitForTimeout(ms);
  await page.emulateMedia({ colorScheme: "dark" });

  // 1. The runner's picker page (auth'd via ?token=). Empty to start.
  await page.goto(`${runnerUrl}/?token=${RUNNER_TOKEN}`, { waitUntil: "load" });
  await page.locator("#new").waitFor({ timeout: 10_000 });
  await settle(1800);

  // 2. Create a session: the runner makes a git worktree and spawns a token-gated headless worker.
  await page.locator("#new").click();
  const openLink = page.locator("li a.open").first();
  await openLink.waitFor({ state: "visible", timeout: 20_000 });
  // Wait until the worker reports running (its supervisor is up).
  await page.locator("li .status.running").first().waitFor({ timeout: 30_000 }).catch(() => {});
  await settle(2000);

  // 3. Connect to the spawned worker over the token-gated bridge — navigate the same page to its URL so the
  //    recording shows the REAL Weavie app booting on the remote worker (editor, rail, terminal).
  const workerUrl = await openLink.getAttribute("href");
  console.log(`[capture] worker url: ${workerUrl}`);
  await page.goto(workerUrl, { waitUntil: "load" });
  await page.locator("#splash").waitFor({ state: "detached", timeout: 45_000 }).catch(() => {});
  await settle(3500);

  // 4. Back to the picker, then tear the session down (removes the worktree) so the capture leaves no trace.
  await page.goto(`${runnerUrl}/?token=${RUNNER_TOKEN}`, { waitUntil: "load" });
  await page.locator("li .del").first().waitFor({ timeout: 10_000 }).catch(() => {});
  await settle(1200);
  await page.locator("li .del").first().click().catch(() => {});
  await settle(2000);
}

function run(cmd, args) {
  return new Promise((res, rej) => {
    const proc = spawn(cmd, args, { stdio: "inherit" });
    proc.on("exit", (code) => (code === 0 ? res() : rej(new Error(`${cmd} exited with ${code}`))));
    proc.on("error", rej);
  });
}

function freePort() {
  return new Promise((res, rej) => {
    const srv = createServer();
    srv.listen(0, "127.0.0.1", () => {
      const addr = srv.address();
      if (addr === null || typeof addr === "string") {
        rej(new Error("could not allocate a port"));
        return;
      }
      const { port } = addr;
      srv.close(() => res(port));
    });
  });
}

function waitForRunner(proc, timeoutMs) {
  return new Promise((res, rej) => {
    const timer = setTimeout(() => rej(new Error("runner did not report listening in time")), timeoutMs);
    proc.stdout.on("data", (chunk) => {
      if (chunk.toString("utf8").includes("control plane: http://")) {
        clearTimeout(timer);
        res();
      }
    });
    proc.on("exit", (code) => {
      clearTimeout(timer);
      rej(new Error(`runner exited early with code ${code}`));
    });
  });
}

async function main() {
  mkdirSync(outDir, { recursive: true });
  console.log("[capture] building headless host (copies fresh web dist into wwwroot) + runner…");
  await run("dotnet", ["build", headlessProject, "-c", "Debug"]);
  await run("dotnet", ["build", runnerProject, "-c", "Debug"]);

  const port = await freePort();
  const runnerUrl = `http://127.0.0.1:${port}`;
  console.log(`[capture] launching runner on ${runnerUrl} (workspace: ${repoRoot})…`);
  const runner = spawn(
    "dotnet",
    [runnerDll, "--workspace", repoRoot, "--token", RUNNER_TOKEN, "--port", String(port),
     "--bind", "127.0.0.1", "--worker-bind", "127.0.0.1", "--headless", headlessDll],
    { stdio: ["ignore", "pipe", "inherit"] },
  );

  try {
    await waitForRunner(runner, 60_000);
    const browser = await chromium.launch();
    const context = await browser.newContext({ viewport, recordVideo: { dir: outDir, size: viewport } });
    const page = await context.newPage();
    page.on("console", (msg) => console.log(`[page:${msg.type()}] ${msg.text()}`));
    page.on("pageerror", (err) => console.log(`[page:error] ${err.message}`));

    await tour(page, runnerUrl);

    const video = page.video();
    await context.close();
    await browser.close();
    const webm = video ? await video.path() : null;
    console.log(`\n[capture] recording: ${webm ?? "(none)"}`);
  } finally {
    runner.kill("SIGINT");
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
