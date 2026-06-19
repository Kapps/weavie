// One-command visual capture: builds the headless host, launches it against the repo workspace, drives the
// REAL app in headless Chromium, and records a .webm — so any change can be demonstrated in a browser with
// no native shell. `npm run capture` builds the web first (see package.json), then runs this, which builds
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

  // Start in DARK. The appearance mode defaults to `system`, so the web resolves the active polarity from the
  // OS `prefers-color-scheme` — emulating a dark OS renders Weavie Dark.
  await page.emulateMedia({ colorScheme: "dark" });
  await settle(1400);

  // Open a couple of workspace files through the Omnibar "Go to File" so the editor shows real,
  // syntax-highlighted code: the new light-theme source (TypeScript) and the hook protocol (C#).
  async function openFile(query) {
    const omnibar = page.locator(".tb-omnibar-input");
    if (!(await omnibar.count())) return;
    await omnibar.click();
    await omnibar.fill("");
    await omnibar.pressSequentially(query, { delay: 70 });
    await settle(750);
    const row = page.locator(".tb-omnibar-row").first();
    if (await row.isVisible().catch(() => false)) {
      await row.click();
      await settle(1100);
    } else {
      await page.keyboard.press("Escape").catch(() => {});
    }
  }
  await openFile("weavie-light.ts");
  await openFile("HookProtocol.cs");

  // Reveal the file tree (left-docked browser overlay) so "files and such" sit on screen with the editor.
  const filesBtn = page.locator(".browser-toggle");
  if (await filesBtn.isVisible().catch(() => false)) {
    await filesBtn.click();
    await settle(900);
    // Expand the first top-level folder to make the tree look lived-in.
    const firstFolder = page.locator(".browser-row").first();
    if (await firstFolder.isVisible().catch(() => false)) {
      await firstFolder.click().catch(() => {});
    }
    await settle(900);
  }

  // Hold on dark so the before-state is clear.
  await settle(1600);

  // Flip the OS to LIGHT. With `theme.mode: system`, the controller's matchMedia listener re-themes the editor,
  // terminal, chrome, and file tree to Weavie Light in place — no reload. This is the feature under test.
  await page.emulateMedia({ colorScheme: "light" });
  await settle(3500);

  // Toggle dark → light once more so the live switch is unmistakable; end on light.
  await page.emulateMedia({ colorScheme: "dark" });
  await settle(1800);
  await page.emulateMedia({ colorScheme: "light" });
  await settle(3500);
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
