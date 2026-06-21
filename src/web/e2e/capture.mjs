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

  // ── FIX UNDER TEST: editor state must follow the active session, never leak across worktrees ──────────────
  // The bug: an `editor-session-changed` carries no session identity, so a debounced tab-set update produced
  // while a worktree session was active could land after the user switched back to the primary — and the host
  // would attribute (and persist) the worktree's file paths under the PRIMARY session. On the next launch the
  // primary editor tries to restore a path that's outside its checkout, the file provider refuses it
  // out-of-root, and the user gets "Unable to read file … nonexistent file" over a BLANK editor.
  //
  // The fix stamps each editor-session message with its owning session id (the host drops a mismatched stale
  // write) and root-scopes the restore (a tab outside the session's tree is never reopened). The observable
  // contract this tour drives: after a session switch, every open editor working copy
  // (window.__WEAVIE_EDITOR_REFS__) is rooted in the ACTIVE session's tree — no foreign worktree path leaks in.
  const claudeSurface = page.locator('.terminal-surface[data-kind="terminal:claude"]');
  await claudeSurface.locator(".xterm").waitFor({ state: "visible", timeout: 20_000 });
  await settle(3000);

  // On-page HUD + an editor-state reader: which file working copies are open, and whether any sit OUTSIDE the
  // primary checkout (a leaked worktree path — the symptom the fix prevents). The primary root is the one the
  // host injected at load (window.__WEAVIE_LSP__.workspace).
  await page.evaluate(() => {
    const hud = document.createElement("div");
    hud.id = "editor-iso-hud";
    hud.style.cssText =
      "position:fixed;left:50%;top:14px;transform:translateX(-50%);z-index:99999;max-width:92vw;" +
      "font:600 13px/1.55 monospace;color:#fff;background:rgba(0,0,0,.85);padding:9px 14px;" +
      "border-radius:8px;border:1px solid rgba(255,255,255,.25);white-space:pre;text-align:center;";
    document.body.appendChild(hud);

    const primaryRoot = (window.__WEAVIE_LSP__?.workspace ?? "").replace(/\\/g, "/").replace(/\/$/, "");
    const underPrimary = (uri) => {
      // The ref keys are file:// URIs; decode to a comparable path and test against the primary root.
      const p = decodeURIComponent(uri).replace(/^file:\/\//, "").replace(/^\/([A-Za-z]:)/, "$1").replace(/\\/g, "/");
      return primaryRoot.length > 0 && p.toLowerCase().startsWith(primaryRoot.toLowerCase());
    };
    window.__readEditor = () => {
      const refs = window.__WEAVIE_EDITOR_REFS__ ?? new Map();
      const open = [...refs.keys()];
      const names = open.map((u) => decodeURIComponent(u).split("/").pop());
      const foreign = open.filter((u) => !underPrimary(u));
      // Is the visible editor actually showing text (not a blank/failed-open pane)?
      const lines = document.querySelector(".monaco-editor .view-lines");
      const textLen = lines ? (lines.textContent ?? "").length : 0;
      return { open: names, foreignCount: foreign.length, textLen };
    };
    window.__setHud = (label, expectClean) => {
      const m = window.__readEditor();
      const ok = !expectClean || (m.foreignCount === 0 && m.textLen > 0);
      hud.style.borderColor = expectClean ? (ok ? "#3ad07a" : "#ff5d5d") : "rgba(255,255,255,.25)";
      hud.textContent =
        `${label}\nopen working copies: [${m.open.join(", ") || "—"}]   editor text: ${m.textLen} chars\n` +
        (expectClean
          ? ok
            ? "✓ all open files are in the active session's tree — no leaked worktree path, editor not blank"
            : `⚠ ${m.foreignCount} foreign path(s) / blank editor — the leak the fix prevents`
          : "(worktree session active — its files live outside the primary checkout, as expected)");
      console.log(`[editor-iso] ${label} :: ${JSON.stringify(m)}`);
    };
  });

  // Open a file in SESSION 1 (the primary checkout) via the omnibar "Go to File".
  const openViaOmnibar = async (query) => {
    const input = page.locator(".tb-omnibar-input");
    await input.click();
    await input.fill("");
    await input.type(query, { delay: 25 });
    await settle(900);
    const firstRow = page.locator(".tb-omnibar-row").first();
    await firstRow.waitFor({ state: "visible", timeout: 8000 }).catch(() => {});
    await page.keyboard.press("Enter");
    await settle(1500);
  };
  await openViaOmnibar("EditorSessionStore");
  await settle(1500);
  await page.evaluate(() => window.__setHud("Session 1 (primary) — file open", true));
  await settle(3000);

  // ── Create a 2nd session: a new worktree off HEAD, switched in automatically ──────────────────────────────
  await page.locator(".session-rail-add").click();
  await page.locator(".session-prompt-input").fill("editor-iso-demo");
  await page.locator(".session-prompt-btn-primary").click();
  await page.locator(".session-chip").nth(1).waitFor({ timeout: 30_000 });
  await settle(5000);
  // Open a DIFFERENT file in the worktree session — its working copy lives under the worktree, not the primary.
  await openViaOmnibar("session-store");
  await settle(1500);
  await page.evaluate(() => window.__setHud("Session 2 (worktree) — different file open", false));
  await settle(3000);

  // ── Switch BACK to session 1 — the exact path that used to persist a leaked worktree tab ──────────────────
  await page.locator(".session-chip").first().click();
  await settle(4500); // rebind: release the worktree's working copies, reopen the primary session's tab
  await page.evaluate(() => window.__setHud("Back on Session 1 (primary) — editor rebound", true));
  await settle(5000);
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
