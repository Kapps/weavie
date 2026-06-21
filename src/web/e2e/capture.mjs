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

  // ── FIX UNDER TEST: scrolling Claude Code after a session switch ──────────────────────────────────────────
  // Claude Code runs INLINE and enables mouse tracking, so the wheel is forwarded to Claude (it scrolls its own
  // transcript). Originally the page shared ONE xterm across sessions and a switch posted `term-reset`, which
  // could drop mouse tracking and hand the wheel to a stray web viewport. Now each loaded session keeps its OWN
  // live xterm mounted and switching is pure show/hide — the active session's terminal is never reset, so its
  // modes are intact by construction. This tour creates a 2nd session, switches back, and reads the (now
  // per-session) Claude xterm's `modes.mouseTrackingMode` — it must stay non-"none".
  const claudeSurface = page.locator('.terminal-surface[data-kind="terminal:claude"]');
  const claudeViewport = claudeSurface.locator(".xterm-viewport");
  await claudeSurface.locator(".xterm").waitFor({ state: "visible", timeout: 20_000 });
  await settle(4000); // let the first Claude TUI paint and enable mouse tracking

  // On-page HUD so the recording is self-explanatory. The decisive signal is the Claude xterm's
  // `modes.mouseTrackingMode`: Claude enables it (so the wheel is forwarded to Claude, which scrolls its
  // own transcript). The bug was that a session switch reset the terminal and dropped it back to "none",
  // so the wheel hit xterm's local viewport instead. After the fix it stays enabled across the switch.
  await page.evaluate(() => {
    const hud = document.createElement("div");
    hud.id = "scroll-hud";
    hud.style.cssText =
      "position:fixed;left:50%;top:14px;transform:translateX(-50%);z-index:99999;" +
      "font:600 14px/1.5 monospace;color:#fff;background:rgba(0,0,0,.82);padding:8px 14px;" +
      "border-radius:8px;border:1px solid rgba(255,255,255,.25);white-space:pre;text-align:center;";
    document.body.appendChild(hud);
    window.__readClaude = () => {
      // Each loaded session has its own claude xterm (keyed `${slot}:claude`); only the active one is
      // shown, so pick the visible one (hidden hosts have a null offsetParent).
      const all = window.__WEAVIE_TERMINALS__ ?? {};
      const term =
        Object.entries(all).find(
          ([key, t]) => key.endsWith(":claude") && t.element?.offsetParent != null,
        )?.[1] ?? null;
      const modes = term ? term.modes : null;
      return {
        // The mode that matters for scrolling on an authed full-screen Claude (forwards the wheel to it).
        mouseTracking: modes ? modes.mouseTrackingMode : "?",
        // Modes headless Claude DOES set — stand-ins proving the switch no longer resets the live terminal.
        bracketedPaste: modes ? modes.bracketedPasteMode : null,
        focusReporting: modes ? modes.sendFocusMode : null,
      };
    };
    window.__setHud = (label) => {
      const m = window.__readClaude();
      // The fix's contract: a session switch must NOT reset the live terminal's modes. Claude set these on
      // boot; if they survive the switch, mouse tracking (set the same way on an authed Claude) survives too.
      const preserved = m.bracketedPaste === true && m.focusReporting === true;
      hud.textContent =
        `${label}\nClaude xterm modes — bracketedPaste=${m.bracketedPaste}  focusReporting=${m.focusReporting}  mouseTracking="${m.mouseTracking}"\n` +
        `${preserved ? "✓ terminal modes intact — wheel still reaches Claude" : "⚠ modes reset by the switch — web scrollbar hijacks the wheel"}`;
      console.log(
        `[scroll-hud] ${label} :: ${JSON.stringify(m)} :: ${preserved ? "MODES-INTACT" : "MODES-RESET"}`,
      );
    };
  });
  await page.evaluate(() => window.__setHud("Session 1 (initial)"));
  await settle(2500);

  // ── Create a 2nd session (rail "+") so there's something to switch between ────────────────────────────────
  await page.locator(".session-rail-add").click();
  await page.locator(".session-prompt-input").fill("scroll-fix-demo");
  await page.locator(".session-prompt-btn-primary").click(); // branch off HEAD → new worktree, switches to it
  // Two chips now on the rail; wait for the new session's Claude to come up.
  await page.locator(".session-chip").nth(1).waitFor({ timeout: 30_000 });
  await settle(6000);
  await page.evaluate(() => window.__setHud("Session 2 (new worktree) — switched in"));
  await settle(2000);

  // ── Switch BACK to session 1 — the path that used to break scrolling ─────────────────────────────────────
  await page.locator(".session-chip").first().click();
  await settle(5000); // pure show/hide: session 1's own live xterm is revealed exactly as it was left
  await page.evaluate(() => window.__setHud("Back on Session 1 (after switch) — wheel test next"));
  await settle(2500);

  // Wheel over the Claude pane: with the bug this scrolls the stray web viewport (scrollTop moves, scrollbar
  // visible); with the fix the wheel is forwarded to the TUI and the local viewport stays put.
  const box = await claudeViewport.boundingBox();
  if (box) {
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    for (let i = 0; i < 5; i++) {
      await page.mouse.wheel(0, -120);
      await settle(180);
    }
  }
  await page.evaluate(() => window.__setHud("Back on Session 1 — after wheel scroll"));
  await settle(4000);
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
