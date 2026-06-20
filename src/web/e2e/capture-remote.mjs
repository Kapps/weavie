// Visual capture for REMOTE SESSIONS (docs/specs/remote-sessions.md): drives the real flow end to end —
// the runner provisions one multi-session Weavie.Headless worker for the workspace; the browser opens it
// (token-gated bridge); then the app's own New Session creates a worktree session ON THE REMOTE BOX via the
// shared HostCore, and the new chip appears in the rail.
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
const BRANCH = "remote-demo";

async function tour(page, runnerUrl) {
  const settle = (ms) => page.waitForTimeout(ms);
  await page.emulateMedia({ colorScheme: "dark" });

  // 1. The runner's landing page (auth'd via ?token=). It ensures the workspace backend and exposes its URL.
  await page.goto(`${runnerUrl}/?token=${RUNNER_TOKEN}`, { waitUntil: "load" });
  const open = page.locator("#open");
  await open.waitFor({ timeout: 10_000 });
  // Wait until the backend reports running, then read its connect URL.
  await page.locator("#status.running").waitFor({ timeout: 30_000 });
  await settle(1500);
  const workerUrl = await open.getAttribute("href");
  console.log(`[capture] backend url: ${workerUrl}`);

  // 2. Connect to the remote worker over the token-gated bridge — the real app boots against it.
  await page.goto(workerUrl, { waitUntil: "load" });
  await page.locator("#splash").waitFor({ state: "detached", timeout: 45_000 }).catch(() => {});
  await settle(2500);

  // 3. The rail shows the primary chip. Click New Session (+) → the prompt.
  const chips = page.locator(".session-chip");
  const before = await chips.count();
  console.log(`[capture] chips before New Session: ${before}`);
  await page.locator(".session-rail-add").click();
  await page.locator(".session-prompt-input").waitFor({ timeout: 8_000 });
  await settle(1200);

  // 4. Name the branch and branch off HEAD — this posts new-session; the REMOTE HostCore creates the worktree.
  await page.locator(".session-prompt-input").fill(BRANCH);
  await settle(800);
  await page.locator(".session-prompt-btn-primary").click();

  // 5. The new remote worktree session appears as a second chip on the rail.
  await page.waitForFunction(
    (n) => document.querySelectorAll(".session-chip").length > n,
    before,
    { timeout: 30_000 },
  );
  console.log(`[capture] chips after New Session: ${await chips.count()}`);
  await settle(3000);
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
