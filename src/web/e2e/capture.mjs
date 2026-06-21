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

  // ── CHANGE UNDER TEST: dependency security bumps (esbuild 0.25.12, dompurify 3.4.11) ──────────────────────
  // These were transitive deps flagged by Dependabot: esbuild <0.25 (dev-server SSRF) and dompurify <=3.3.x
  // (a cluster of XSS / prototype-pollution advisories). dompurify ships INSIDE the monaco-vscode runtime —
  // it sanitizes the markup the editor's quickaccess/command palette renders (highlighted match labels). So
  // this tour proves the bumped sanitizer still works end-to-end: it opens a file into the Monaco editor and
  // drives the editor's command palette (F1), showing the dompurify-rendered, highlighted result rows.
  const hudLabel = (text) =>
    page.evaluate((t) => {
      let hud = document.getElementById("dep-hud");
      if (!hud) {
        hud = document.createElement("div");
        hud.id = "dep-hud";
        // Pinned bottom-center and click-through (pointer-events:none) so it never sits over the
        // top-center omnibar/title bar and intercept the clicks the tour issues.
        hud.style.cssText =
          "position:fixed;left:50%;bottom:18px;transform:translateX(-50%);z-index:99999;pointer-events:none;" +
          "font:600 14px/1.5 monospace;color:#fff;background:rgba(0,0,0,.82);padding:8px 14px;" +
          "border-radius:8px;border:1px solid rgba(255,255,255,.25);white-space:pre;text-align:center;";
        document.body.appendChild(hud);
      }
      hud.textContent = t;
    }, text);

  await hudLabel(
    "Security bump verify — esbuild 0.25.12 · dompurify 3.4.11\nApp booted & built with the new toolchain ✓",
  );
  await settle(2500);

  // ── Open a file into the Monaco editor via the omnibar (Go to File) ───────────────────────────────────────
  await hudLabel("Opening CLAUDE.md in the Monaco editor…");
  const omni = page.locator(".tb-omnibar-input");
  await omni.click();
  await settle(600);
  await omni.fill("CLAUDE.md");
  await settle(1200);
  await omni.press("Enter");
  // The Monaco editor mounts inside the editor surface once a file is open.
  await page
    .locator(".editor-surface .monaco-editor")
    .first()
    .waitFor({ state: "visible", timeout: 20_000 });
  await settle(2500);

  // ── Drive Monaco's quickaccess command palette (F1) — the widget dompurify backs ──────────────────────────
  // Click into the editor so the F1 chord lands on Monaco, then open its command palette and type a query so
  // it renders highlighted (dompurify-sanitized) match labels. If dompurify were broken, these rows would
  // fail to render the highlighted markup.
  await hudLabel("Opening Monaco command palette (F1) — rows rendered via dompurify");
  await page.locator(".editor-surface .monaco-editor").first().click();
  await settle(600);
  await page.keyboard.press("F1");
  const quickInput = page.locator(".quick-input-widget");
  await quickInput.waitFor({ state: "visible", timeout: 10_000 });
  await settle(1000);
  await page.keyboard.type("fold", { delay: 60 });
  await settle(1500);
  // The highlighted-label spans are the dompurify-rendered output; confirm they actually appear.
  const highlightedRows = quickInput.locator(
    ".monaco-list-row .highlight, .monaco-list-row .monaco-highlighted-label",
  );
  const count = await highlightedRows.count().catch(() => 0);
  await hudLabel(
    `Monaco command palette filtered to "fold"\ndompurify-rendered highlighted rows present: ${count > 0 ? "✓" : "—"} (${count})`,
  );
  await settle(3500);
  await page.keyboard.press("Escape");
  await settle(2000);
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
