// Visual capture for REMOTE SESSIONS (docs/specs/remote-sessions.md), end to end: boot the local Weavie,
// register a runner, start a remote session (worktree created on the runner), and record the remote-badged
// chip joining the rail.
//
// Run from src/web after `pnpm run build`:  node e2e/capture-remote.mjs
// Builds Weavie.Headless + Weavie.Runner, records to e2e/.recordings/.

import { spawn } from "node:child_process";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "@playwright/test";

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(webRoot, "..", "..");
const headlessProject = join(repoRoot, "src", "Weavie.Headless", "Weavie.Headless.csproj");
const runnerProject = join(repoRoot, "src", "Weavie.Runner", "Weavie.Runner.csproj");
const headlessDll = join(
  repoRoot,
  "src",
  "Weavie.Headless",
  "bin",
  "Debug",
  "net10.0",
  "Weavie.Headless.dll",
);
const runnerDll = join(
  repoRoot,
  "src",
  "Weavie.Runner",
  "bin",
  "Debug",
  "net10.0",
  "Weavie.Runner.dll",
);
const outDir = join(webRoot, "e2e", ".recordings");
const viewport = { width: 1280, height: 800 };
const RUNNER_TOKEN = "capturetoken";
// The remote gets its own repo so its worktrees don't collide with the local host's on this one machine.
const remoteRepo = join("/tmp", "weavie-remote-demo");

async function tour(page, localPageUrl, runnerUrl) {
  const settle = (ms) => page.waitForTimeout(ms);
  await page.emulateMedia({ colorScheme: "dark" });

  // 1. Boot the local Weavie.
  await page.goto(localPageUrl, { waitUntil: "load" });
  await page
    .locator("#splash")
    .waitFor({ state: "detached", timeout: 45_000 })
    .catch(() => {});
  await settle(2500);
  const before = await page.locator(".session-chip").count();
  console.log(`[capture] local chips: ${before}`);

  // 2. New Session → choose "Add remote agent…".
  await page.locator(".session-rail-add").click();
  await page.locator(".session-prompt-select").waitFor({ timeout: 8_000 });
  await settle(1200);
  await page.locator(".session-prompt-select").selectOption("__add__");
  await page.locator(".session-prompt-input").first().waitFor({ timeout: 8_000 });
  await settle(1000);

  // 3. Register the runner (name / URL / token).
  const inputs = page.locator(".session-prompt-input");
  await inputs.nth(0).fill("devbox");
  await inputs.nth(1).fill(runnerUrl);
  await inputs.nth(2).fill(RUNNER_TOKEN);
  await settle(1200);
  await page.locator(".session-prompt-btn-primary").click();

  // 4. Pick the now-available remote location.
  await page.locator(".session-prompt-select").waitFor({ timeout: 15_000 });
  await settle(1200);
  await page.locator(".session-prompt-select").selectOption("remote:devbox");
  await settle(800);
  await page.locator(".session-prompt-input").fill("remote-feature");
  await settle(800);
  await page.locator(".session-prompt-btn-primary").click();

  // 5. The remote-badged chip joins the rail; the worktree was created on the runner.
  await page.locator(".session-chip.remote").first().waitFor({ timeout: 30_000 });
  await page.waitForFunction((n) => document.querySelectorAll(".session-chip").length > n, before, {
    timeout: 30_000,
  });
  console.log(
    `[capture] chips after remote New Session: ${await page.locator(".session-chip").count()}`,
  );
  await settle(3500);
}

function run(cmd, args, opts = {}) {
  return new Promise((res, rej) => {
    const proc = spawn(cmd, args, { stdio: "inherit", ...opts });
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

function waitForLine(proc, expected, timeoutMs) {
  return new Promise((res, rej) => {
    let output = "";
    const timer = setTimeout(() => rej(new Error(`did not see "${expected}" in time`)), timeoutMs);
    proc.stdout.on("data", (chunk) => {
      output += chunk.toString("utf8");
      const match =
        typeof expected === "string" ? output.includes(expected) : output.match(expected);
      if (match) {
        clearTimeout(timer);
        res(Array.isArray(match) ? match[1] : undefined);
      }
    });
    proc.on("exit", (code) => {
      clearTimeout(timer);
      rej(new Error(`process exited early with code ${code}`));
    });
  });
}

async function setupRemoteRepo() {
  rmSync(remoteRepo, { recursive: true, force: true });
  mkdirSync(remoteRepo, { recursive: true });
  writeFileSync(join(remoteRepo, "README.md"), "# remote demo workspace\n");
  await run("git", ["-C", remoteRepo, "init", "-q"]);
  await run("git", ["-C", remoteRepo, "config", "user.email", "demo@weavie.dev"]);
  await run("git", ["-C", remoteRepo, "config", "user.name", "weavie demo"]);
  await run("git", ["-C", remoteRepo, "add", "."]);
  await run("git", ["-C", remoteRepo, "commit", "-q", "-m", "init"]);
}

async function main() {
  mkdirSync(outDir, { recursive: true });
  console.log("[capture] building headless host (copies fresh web dist into wwwroot) + runner…");
  await run("dotnet", ["build", headlessProject, "-c", "Debug"]);
  await run("dotnet", ["build", runnerProject, "-c", "Debug"]);
  await setupRemoteRepo();

  const localPort = await freePort();
  const runnerPort = await freePort();
  const localUrl = `http://127.0.0.1:${localPort}`;
  const runnerUrl = `http://127.0.0.1:${runnerPort}`;

  console.log(`[capture] launching local headless on ${localUrl} (workspace: ${repoRoot})…`);
  const local = spawn("dotnet", [headlessDll], {
    env: { ...process.env, WEAVIE_SERVE_PORT: String(localPort), WEAVIE_SERVE_WORKSPACE: repoRoot },
    stdio: ["ignore", "pipe", "inherit"],
  });

  console.log(`[capture] launching runner on ${runnerUrl} (workspace: ${remoteRepo})…`);
  const runner = spawn(
    "dotnet",
    [
      runnerDll,
      "--workspace",
      remoteRepo,
      "--token",
      RUNNER_TOKEN,
      "--port",
      String(runnerPort),
      "--bind",
      "127.0.0.1",
      "--worker-bind",
      "127.0.0.1",
      "--headless",
      headlessDll,
    ],
    { stdio: ["ignore", "pipe", "inherit"] },
  );

  try {
    const [localPageUrl] = await Promise.all([
      waitForLine(local, /open\s+(http:\/\/\S+)/, 60_000),
      waitForLine(runner, "control plane: http://", 60_000),
    ]);
    const browser = await chromium.launch();
    const context = await browser.newContext({
      viewport,
      recordVideo: { dir: outDir, size: viewport },
    });
    const page = await context.newPage();
    page.on("console", (msg) => console.log(`[page:${msg.type()}] ${msg.text()}`));
    page.on("pageerror", (err) => console.log(`[page:error] ${err.message}`));

    await tour(page, localPageUrl, runnerUrl);

    const video = page.video();
    await context.close();
    await browser.close();
    const webm = video ? await video.path() : null;
    console.log(`\n[capture] recording: ${webm ?? "(none)"}`);
  } finally {
    runner.kill();
    local.kill();
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
