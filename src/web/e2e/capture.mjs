// One-command visual capture: builds the headless host, launches it against the repo workspace, drives the
// REAL app in headless Chromium, and records a .webm — so any change can be demonstrated in a browser with
// no native shell. `pnpm run capture` builds the web first (see package.json), then runs this, which builds
// Weavie.Headless (copying the fresh dist into its wwwroot) and records. Build-then-record by construction,
// so the clip is never stale.
//
// PER CHANGE: edit tour() below to drive the exact UI path your change affects (open the view, trigger the
// fix) — the recording must SHOW the feature/fix in action, not just the app booting. The .webm lands in
// e2e/.recordings/ (gitignored); share that file in your reply.

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

// ── EDIT PER CHANGE ──────────────────────────────────────────────────────────────────────────────────────
// Drive the exact UI path your change affects so the recording demonstrates it. The default flow (Omnibar
// Go-to-File opening a file into the editor) is just a placeholder — replace it with the steps that exercise
// your feature/fix.
async function tour(page) {
  const settle = (ms) => page.waitForTimeout(ms);

  // The splash sits over the app until the editor is ready; wait so we record the settled UI.
  await page
    .locator("#splash")
    .waitFor({ state: "detached", timeout: 45_000 })
    .catch(() => {});
  await settle(1200);

  // Record in DARK (the appearance mode defaults to `system`, resolved from the OS `prefers-color-scheme`).
  await page.emulateMedia({ colorScheme: "dark" });
  await settle(1200);

  // ── FEATURE UNDER TEST: resume the previous Claude session on relaunch ────────────────────────────────────
  // This change makes the host launch `claude` with `--resume <id>` (or `--session-id <id>` the first time),
  // keyed by the session's working directory, so reopening a session continues its prior conversation. It's a
  // host launch-flag behavior with no new web UI — the authoritative functional proof is temp/resume-proof.mjs
  // (run 1 logs `--session-id`, run 2 logs `--resume`, same id). This capture is the regression check that the
  // new launch path still boots the real headless app and brings up the Claude Code pane (the workspace is
  // pointed at temp/proof-ws, which already holds a persisted session id, so the host boots with `--resume`).
  await page
    .locator(".pane-label", { hasText: "Claude Code" })
    .first()
    .waitFor({ timeout: 15_000 });
  await settle(800);

  // The Claude pane is a real xterm bound to the real claude PTY launched via the resume code path; let it
  // render its startup so the recording shows the agent pane live under the resumed session.
  const claudePane = page
    .locator(".pane", { has: page.locator(".pane-label", { hasText: "Claude Code" }) })
    .first();
  await claudePane.locator(".xterm").waitFor({ state: "visible", timeout: 15_000 });
  await claudePane.click().catch(() => {});
  await settle(7000);
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
    const browser = await chromium.launch();
    const context = await browser.newContext({
      viewport,
      recordVideo: { dir: outDir, size: viewport },
    });
    const page = await context.newPage();
    page.on("console", (msg) => console.log(`[page:${msg.type()}] ${msg.text()}`));
    page.on("pageerror", (err) => console.log(`[page:error] ${err.message}`));
    await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: "load" });

    await tour(page);

    const video = page.video();
    await context.close(); // finalize the .webm
    await browser.close();
    const webm = video ? await video.path() : null;
    console.log(`\n[capture] recording: ${webm ?? "(none)"}`);
  } finally {
    host.kill("SIGINT");
  }
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
