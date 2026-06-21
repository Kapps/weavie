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
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
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
const viewport = { width: 1280, height: 800 };

// This capture demonstrates the post-turn per-hunk review (docs/specs/turn-review.md). That flow is driven by
// host→web `turn-changes` / `turn-diff` messages a real Claude turn would emit; we inject them directly via
// window.__weavieReceive (the local-backend delivery hook) so the UI can be shown without an authed Claude.
// Two files with two hunks each show the web surface: the inline toolbar, per-hunk Keep (web-only mark +
// de-emphasis + hunk→hunk→file auto-advance), the ← / → file walk with marks that survive reopening, and
// Keep-all. (Revert — the one disk-touching action — is left to the Core unit tests, since these injected files
// aren't in the host's real change tracker, so a round-tripped reject-hunk would correctly guard-mismatch.)
const reviewWorkspace = mkdtempSync(join(tmpdir(), "weavie-review-"));
// `current` = exactly what's written to disk (so it equals the editor's live model); `baseline` differs in two
// separate places (an equal line between them ⇒ two hunks).
const FILE_A_CURRENT = "line one\nline two\nline three\nline four\nline five\n";
const FILE_A_BASELINE = "line one\nOLD two\nline three\nOLD four\nline five\n";
const FILE_B_CURRENT = "alpha\nbravo\ncharlie\ndelta\n";
const FILE_B_BASELINE = "alpha\nBRAVO\ncharlie\ndelta\n";
writeFileSync(join(reviewWorkspace, "a.txt"), FILE_A_CURRENT);
writeFileSync(join(reviewWorkspace, "b.txt"), FILE_B_CURRENT);
const fileA = join(reviewWorkspace, "a.txt");
const fileB = join(reviewWorkspace, "b.txt");
// Record against the prepared workspace by default; override to point at any workspace.
const workspace = process.env.WEAVIE_CAPTURE_WORKSPACE ?? reviewWorkspace;

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

  // Deliver a host→web message exactly as the bridge would (the local backend's delivery hook).
  const deliver = (msg) => page.evaluate((m) => window.__weavieReceive(JSON.stringify(m)), msg);

  // On-page HUD so the recording narrates itself.
  await page.evaluate(() => {
    const hud = document.createElement("div");
    hud.id = "review-hud";
    hud.style.cssText =
      "position:fixed;left:50%;top:14px;transform:translateX(-50%);z-index:99999;" +
      "font:600 14px/1.5 monospace;color:#fff;background:rgba(0,0,0,.82);padding:8px 14px;" +
      "border-radius:8px;border:1px solid rgba(255,255,255,.25);white-space:pre;text-align:center;max-width:90vw;";
    document.body.appendChild(hud);
    window.__setHud = (label) => {
      hud.textContent = label;
      console.log(`[review-hud] ${label}`);
    };
  });

  // The two files Claude "changed" this turn, with their first-change lines (1-based).
  const reviewFiles = [
    { path: fileA, name: "a.txt", added: 2, removed: 2, line: 2 },
    { path: fileB, name: "b.txt", added: 1, removed: 1, line: 2 },
  ];

  // Inject the review set + both files' per-turn diffs (baseline vs current). a.txt opens as a preview tab and
  // the inline applied toolbar arms over the editor — the 2D review navigator (↑/↓ hunks, ← / → files) the
  // spec describes. (Pre-injecting b.txt's diff so it renders when the Keep walk auto-advances into it; a real
  // turn's per-file diffs arrive the same way over the bridge. Revert — the one disk-touching action — is left
  // to the Core unit tests, since these injected files aren't in the host's change tracker.)
  await deliver({ type: "open-file", path: fileA, content: "", line: 2, preview: true });
  await settle(1500);
  await deliver({ type: "turn-changes", open: false, files: reviewFiles });
  await deliver({
    type: "turn-diff",
    path: fileA,
    name: "a.txt",
    baseline: FILE_A_BASELINE,
    current: FILE_A_CURRENT,
  });
  await deliver({
    type: "turn-diff",
    path: fileB,
    name: "b.txt",
    baseline: FILE_B_BASELINE,
    current: FILE_B_CURRENT,
  });
  await page.locator(".weavie-inline-toolbar").waitFor({ timeout: 10_000 });
  await page.evaluate(() =>
    window.__setHud(
      "Post-turn review armed — a.txt (1/2), two hunks. Toolbar: Keep / Revert / Keep all + ← name (i/N) →",
    ),
  );
  await settle(3000);

  // Keep the FIRST hunk (Ctrl+Enter): web-only mark — it de-emphasises (grey) and the cursor auto-advances to
  // the next PENDING hunk. No disk write. (The keybinding resolver is a document-level capture listener, so it
  // fires without clicking into Monaco — which would move the cursor off the first hunk.)
  await page.keyboard.press("Control+Enter");
  await page.evaluate(() =>
    window.__setHud(
      "Kept hunk 1 (Ctrl+Enter) — it greys out, cursor auto-advances to the next pending hunk",
    ),
  );
  await settle(3000);

  // Keep the SECOND hunk: no pending hunk left in a.txt, so the walk auto-advances to the next pending FILE
  // (b.txt), landing on its first change — hunk → hunk → file, all from Ctrl+Enter.
  await page.keyboard.press("Control+Enter");
  await settle(2500);
  await page.evaluate(() =>
    window.__setHud(
      "Kept hunk 2 — no pending hunks left in a.txt, so Keep auto-advanced to the next file: b.txt (2/2)",
    ),
  );
  await settle(3000);

  // Walk files manually too: Ctrl+← back to a.txt shows its Keep marks SURVIVED leaving + reopening (both
  // hunks still grey), the persistence the spec requires.
  await page.keyboard.press("Control+ArrowLeft");
  await settle(2500);
  await page.evaluate(() =>
    window.__setHud(
      "Ctrl+← back to a.txt — both hunks are still grey: the Keep marks survived leaving + reopening the file",
    ),
  );
  await settle(3000);

  // Keep-all clears the whole accumulated set in one action (the debt-clearer) — the inline markers + toolbar
  // vanish across every file.
  await page.locator(".weavie-inline-keepall").click();
  await settle(2000);
  await page.evaluate(() =>
    window.__setHud(
      "Keep all — the whole review set cleared in one action; the inline markers are gone",
    ),
  );
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
