import { existsSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { activeSessionSlot, createSession, waitForSessionSwitch } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// VIDEO TOUR (scratch — not a committed spec): a session whose working directory is deleted out from under it
// ends itself, with one plain notice and none of git's missing-directory words, and stays gone across a reload.

declare global {
  interface Window {
    __TOASTS__?: string[];
  }
}

const RAW_GIT = "Git working directory does not exist";
const RAW_FILES = "Couldn't load workspace files";

test.use({
  preNavigate: {
    run: async (page) => {
      // Record every toast message that ever appears, so a raw git error can't slip past between polls.
      await page.addInitScript(() => {
        window.__TOASTS__ = [];
        const record = (): void => {
          for (const element of document.querySelectorAll(".toast .toast-msg")) {
            const text = element.textContent ?? "";
            if (text.length > 0 && !window.__TOASTS__?.includes(text)) {
              window.__TOASTS__?.push(text);
            }
          }
        };
        const start = (): void => {
          record();
          new MutationObserver(record).observe(document.documentElement, {
            childList: true,
            subtree: true,
            characterData: true,
          });
        };
        if (document.documentElement === null) {
          document.addEventListener("DOMContentLoaded", start, { once: true });
        } else {
          start();
        }
      });
    },
  },
});

const hold = (page: Page, ms: number) => page.waitForTimeout(ms);
const toasts = (page: Page) => page.evaluate(() => window.__TOASTS__ ?? []);

// A tour-only on-screen caption so the clip reads as a narrated scenario.
async function caption(page: Page, text: string, ms = 1800): Promise<void> {
  await page.evaluate((value) => {
    let element = document.querySelector<HTMLElement>("#tour-caption");
    if (element === null) {
      element = document.createElement("div");
      element.id = "tour-caption";
      element.style.cssText =
        "position:fixed;left:50%;bottom:18px;transform:translateX(-50%);z-index:2147483647;" +
        "background:rgba(12,14,18,.93);color:#f4f6fa;border:1px solid #54c6a4;border-radius:8px;" +
        "padding:10px 18px;font:600 16px/1.35 system-ui,sans-serif;max-width:78vw;text-align:center;" +
        "pointer-events:none;box-shadow:0 6px 24px rgba(0,0,0,.55)";
      document.body.appendChild(element);
    }
    element.textContent = value;
  }, text);
  await hold(page, ms);
}

async function showToast(page: Page, text: string): Promise<void> {
  // Hovering pauses a timed toast's clock, so the notice stays on camera while it's read.
  const toast = page.locator(".toast", { hasText: text });
  await expect(toast).toHaveCount(1);
  await toast.hover();
}

test("a background session closes itself when its worktree is deleted in a shell", async ({
  page,
  weavie,
}) => {
  test.setTimeout(180_000);
  const chips = page.locator(".session-chip");
  await page.emulateMedia({ colorScheme: "dark" });

  await caption(page, "One session on the rail: the workspace checkout.");
  const workspaceSlot = await activeSessionSlot(page);

  await caption(page, "New Session forks a second session onto its own worktree…");
  await createSession(page, { branch: "e2e/vanishing", provider: "claude" });
  await expect(chips).toHaveCount(2);
  await waitForSessionSwitch(page, workspaceSlot);
  const [worktree] = sessionWorktrees(weavie.workspace);
  expect(worktree).toBeTruthy();

  await caption(page, "…two chips on the rail. Back to the workspace session.");
  await chips.first().click();
  await expect(chips.first()).toHaveClass(/\bactive\b/);
  await hold(page, 800);

  await caption(
    page,
    "Now delete that worktree from a shell — outside Weavie, not via its delete command.",
    2200,
  );
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  await shell.locator(".shell-tab-main").click();
  await page.keyboard.type(`rm -rf ${worktree}`, { delay: 35 });
  await hold(page, 900);
  await page.keyboard.press("Enter");
  await expect.poll(() => existsSync(worktree), { timeout: 20_000 }).toBe(false);

  await caption(page, "The folder is gone. Weavie ends the session itself…", 1200);
  await expect(chips).toHaveCount(1);
  await expect(chips.first()).toHaveAttribute("data-session-slot", workspaceSlot);

  await caption(page, "…with one plain notice — and no raw git error.", 1000);
  await showToast(page, "was closed: its worktree");
  await hold(page, 3500);

  const seen = await toasts(page);
  expect(seen).toContain(`Session 'e2e/vanishing' was closed: its worktree ${worktree} no longer exists.`);
  expect(seen.filter((t) => t.includes("was closed: its worktree"))).toHaveLength(1);
  expect(seen.filter((t) => t.includes(RAW_GIT) || t.includes(RAW_FILES))).toEqual([]);

  await caption(page, "Reload the page — a reconnect used to replay the raw git error…", 2000);
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await hold(page, 6000);
  const afterReload = await toasts(page);
  expect(afterReload.filter((t) => t.includes(RAW_GIT) || t.includes(RAW_FILES))).toEqual([]);
  await expect(chips).toHaveCount(1);
  await caption(page, "…it does not come back. One session, no error.", 3000);
});

test("the active session's own worktree vanishing lands the user back on the workspace", async ({
  page,
  weavie,
}) => {
  test.setTimeout(180_000);
  const chips = page.locator(".session-chip");
  await page.emulateMedia({ colorScheme: "dark" });
  const workspaceSlot = await activeSessionSlot(page);

  await caption(page, "A forked session, and the user is looking at it.");
  await createSession(page, { branch: "e2e/active-vanishing", provider: "claude" });
  await expect(chips).toHaveCount(2);
  await waitForSessionSwitch(page, workspaceSlot);
  const [worktree] = sessionWorktrees(weavie.workspace);
  await hold(page, 1200);

  await caption(page, "Its folder is removed underneath it (git worktree remove --force).", 2000);
  rmSync(worktree, { recursive: true, force: true });

  await caption(page, "The dead session closes and selection falls back to the workspace.", 1000);
  await expect(chips).toHaveCount(1);
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    workspaceSlot,
  );
  await showToast(page, "was closed: its worktree");
  await hold(page, 3500);
  const seen = await toasts(page);
  expect(seen.filter((t) => t.includes(RAW_GIT) || t.includes(RAW_FILES))).toEqual([]);
  await caption(page, "One notice, and a live workspace session to keep working in.", 2500);
});

test("the workspace checkout's own folder vanishing keeps its slot and says so", async ({
  page,
  weavie,
}) => {
  test.setTimeout(180_000);
  const chips = page.locator(".session-chip");
  await page.emulateMedia({ colorScheme: "dark" });

  await caption(page, "The exception: the workspace's OWN checkout folder is deleted.", 2000);
  rmSync(weavie.workspace, { recursive: true, force: true });

  await caption(page, "Weavie can't close that one — it reports the workspace failure…", 1500);
  await showToast(page, "This workspace's folder no longer exists");
  await expect(
    page.locator(".toast", {
      hasText: `This workspace's folder no longer exists: ${weavie.workspace}. Open a workspace that does.`,
    }),
  ).toHaveCount(1);
  await hold(page, 3500);

  await caption(page, "…and the session stays on the rail.", 2000);
  await expect(chips).toHaveCount(1);
  const seen = await toasts(page);
  expect(seen.filter((t) => t.includes(RAW_GIT))).toEqual([]);
  expect(seen.filter((t) => t.includes("no longer exists"))).toHaveLength(1);
});

test("a git failure that is not a missing folder still reports in full", async ({
  page,
  weavie,
}) => {
  test.setTimeout(180_000);
  const chips = page.locator(".session-chip");
  await page.emulateMedia({ colorScheme: "dark" });

  await caption(page, "A session whose folder is still there, but whose git is broken…", 2000);
  await createSession(page, { branch: "e2e/broken-git", provider: "claude" });
  await expect(chips).toHaveCount(2);
  const [worktree] = sessionWorktrees(weavie.workspace);
  // The worktree's .git file points at its git dir; corrupting it fails git while the directory still exists.
  writeFileSync(join(worktree, ".git"), "gitdir: /nonexistent/not-a-git-dir\n");
  writeFileSync(join(worktree, "poke.txt"), "poke\n");

  await caption(page, "…still says exactly what went wrong, and keeps its session.", 2500);
  await expect(page.locator(".toast.toast-error, .toast.toast-warn")).toHaveCount(1, {
    timeout: 30_000,
  });
  await showToast(page, "");
  await hold(page, 3000);
  const seen = await toasts(page);
  console.log(`[tour] broken-git toasts: ${JSON.stringify(seen)}`);
  expect(seen.filter((t) => t.includes("was closed: its worktree"))).toEqual([]);
  await expect(chips).toHaveCount(2);
});
