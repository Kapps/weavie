// One-command visual capture: builds the headless host, launches it against the repo workspace, drives the
// real app in headless Chromium, and records a .webm. `pnpm run capture` builds the web first (see
// package.json), then runs this, which builds Weavie.Headless (copying the fresh dist into its wwwroot) and
// records — so the clip is never stale.
//
// PER CHANGE: don't edit the tour here — it's the committed frame of reference and stays frozen. Drop a
// gitignored `tour.local.mjs` next to this file exporting `async function tour(page)`; capture picks it up
// automatically and falls back to defaultTour() below when absent. The recording must SHOW the feature/fix
// in action. The .webm lands in e2e/.recordings/ (gitignored); share it in your reply.

import { spawn } from "node:child_process";
import { mkdirSync } from "node:fs";
import { createServer } from "node:net";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "@playwright/test";

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(webRoot, "..", "..");
const hostProject = join(repoRoot, "src", "Weavie.Headless", "Weavie.Headless.csproj");
const hostDll = join(
  repoRoot,
  "src",
  "Weavie.Headless",
  "bin",
  "Debug",
  "net10.0",
  "Weavie.Headless.dll",
);
const outDir = join(webRoot, "e2e", ".recordings");
// Record against the repo by default; override to point at any workspace.
const workspace = process.env.WEAVIE_CAPTURE_WORKSPACE ?? repoRoot;
const viewport = { width: 1280, height: 800 };

// ── DEFAULT TOUR (frame of reference — do not customize here) ────────────────────────────────────────────
// Change-agnostic flow: wait out the splash and record the settled app in dark mode. To demonstrate a
// specific feature/fix, export your own `tour(page)` from a gitignored `tour.local.mjs` (see loadTour())
// rather than editing this.
async function defaultTour(page) {
  const settle = (ms) => page.waitForTimeout(ms);

  // The splash sits over the app until the editor is ready; wait so we record the settled UI.
  await page
    .locator("#splash")
    .waitFor({ state: "detached", timeout: 45_000 })
    .catch(() => {});
  await settle(1200);

  // Record in dark mode (appearance defaults to `system`, resolved from `prefers-color-scheme`).
  await page.emulateMedia({ colorScheme: "dark" });
  await settle(2500);
}

// Prefer a gitignored override so per-change tours are never committed; fall back to the default.
async function loadTour() {
  try {
    const mod = await import("./tour.local.mjs");
    if (typeof mod.tour === "function") {
      console.log("[capture] using tour from tour.local.mjs");
      return mod.tour;
    }
    console.log("[capture] tour.local.mjs has no `tour` export — using defaultTour");
  } catch {
    // No override present — that's the normal case.
  }
  return defaultTour;
}
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────

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

function waitForHost(proc, timeoutMs) {
  return new Promise((res, rej) => {
    const timer = setTimeout(
      () => rej(new Error("host did not report listening in time")),
      timeoutMs,
    );
    proc.stdout.on("data", (chunk) => {
      if (chunk.toString("utf8").includes("open  http://")) {
        clearTimeout(timer);
        res();
      }
    });
    proc.on("exit", (code) => {
      clearTimeout(timer);
      rej(new Error(`host exited early with code ${code}`));
    });
  });
}

async function main() {
  mkdirSync(outDir, { recursive: true });
  console.log("[capture] building headless host (copies the fresh web dist into wwwroot)…");
  await run("dotnet", ["build", hostProject, "-c", "Debug"]);

  const port = await freePort();
  console.log(`[capture] launching host on 127.0.0.1:${port} (workspace: ${workspace})…`);
  const host = spawn("dotnet", [hostDll], {
    env: { ...process.env, WEAVIE_SERVE_PORT: String(port), WEAVIE_SERVE_WORKSPACE: workspace },
    stdio: ["ignore", "pipe", "inherit"],
  });

  try {
    await waitForHost(host, 60_000);
    // When the project pins a Playwright newer than the pre-installed browser build, launch the bundled
    // Chromium directly (WEAVIE_CHROMIUM) instead of downloading a matching build.
    const executablePath = process.env.WEAVIE_CHROMIUM || undefined;
    const browser = await chromium.launch({ executablePath });
    try {
      const context = await browser.newContext({
        viewport,
        recordVideo: { dir: outDir, size: viewport },
      });
      const page = await context.newPage();
      page.on("console", (msg) => console.log(`[page:${msg.type()}] ${msg.text()}`));
      page.on("pageerror", (err) => console.log(`[page:error] ${err.message}`));
      await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: "load" });

      const tour = await loadTour();
      await tour(page);

      const video = page.video();
      await context.close(); // finalize the .webm
      const webm = video ? await video.path() : null;
      console.log(`\n[capture] recording: ${webm ?? "(none)"}`);
    } finally {
      // A failed tour must still tear the browser down, or the run hangs until something kills it.
      await browser.close();
    }
  } finally {
    host.kill("SIGINT");
  }
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
