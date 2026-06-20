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

  // ── FEATURE UNDER TEST: session context menu + worktree deletion ──────────────────────────────────────────
  // This PR adds the rail's right-click menu (Load/Unload + Delete…) and the guarded Delete-session dialog.
  // The multi-session backend is a native-shell (Win/Mac) host capability; the headless host runs a single
  // session and never pushes a `session-list`. So we feed the rail through the SAME seam the native shells
  // use — `window.__weavieReceive`, the host→web message sink — to populate it and to answer the delete
  // classification, then drive the REAL components (SessionRail → ContextMenu → DeleteSessionDialog).
  const receive = (msg) =>
    page.evaluate((m) => window.__weavieReceive(JSON.stringify(m)), msg);

  // 1. Populate the rail: the workspace's primary checkout + a deletable worktree session ("feat/login").
  const primary = {
    id: "primary",
    label: "weavie",
    active: true,
    loaded: true,
    primary: true,
    status: "idle",
    hue: 210,
    monogram: "we",
  };
  const worktree = {
    id: "s-login",
    label: "feat/login",
    active: false,
    loaded: true,
    primary: false,
    status: "working",
    hue: 28,
    monogram: "fl",
  };
  await receive({ type: "session-list", sessions: [primary, worktree] });
  const chip = page.locator(".session-chip").nth(1);
  await chip.waitFor({ state: "visible", timeout: 10_000 });
  await settle(1600);

  // 2. Right-click the worktree chip → the shared command-driven context menu (Unload session / Delete…).
  await chip.click({ button: "right" });
  await page.locator(".context-menu").waitFor({ timeout: 5_000 });
  await settle(1800);

  // 3. Click "Delete…" (the danger row). The page posts delete-session-request; the host normally classifies
  //    the worktree and replies with session-delete-prompt. We answer with "modified" so the dialog shows its
  //    escalated confirm (the "uncommitted changes would be lost" checkbox gate).
  await page.locator(".context-menu-item.danger").click();
  await settle(400);
  await receive({
    type: "session-delete-prompt",
    id: "s-login",
    label: "feat/login",
    state: "modified",
  });

  // 4. Show the guarded "Delete session?" dialog, tick the acknowledgement, then commit via the danger button.
  await page.locator(".confirm-dialog .confirm-title").waitFor({ timeout: 5_000 });
  await settle(1600);
  const ack = page.locator(".confirm-check input[type=checkbox]");
  if (await ack.count()) {
    await ack.check().catch(() => {});
    await settle(900);
  }
  await page.locator(".confirm-btn-danger").click();
  await settle(700);

  // 5. The host would now remove the worktree and re-push the list; emulate that so the chip drops off the rail.
  await receive({ type: "session-list", sessions: [primary] });
  await settle(2500);
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
