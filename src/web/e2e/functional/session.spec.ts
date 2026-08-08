import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import {
  activeSessionSlot,
  createSession,
  runCommand,
  waitForSessionSwitch,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// Session lifecycle through the rail + commands: create (fork a worktree session off the current one),
// switch, unload, and reopen. Sessions and worktrees are a HostCore concern that differs structurally on
// remote (the Runner provisions the worktree), so this runs @cross.
//
// Flaked 2026-07-23 on Windows CI (remote project): https://github.com/Kapps/weavie/actions/runs/29973556071
// — the auto `weavie` fixture's boot-time `#splash` wait timed out at 40s before this test's body ever ran
// (harness/fixtures.ts:82), so this is the pre-existing "setup class" flake in docs/specs/e2e-flake-analysis.md
// (#4/#5), not a defect in this test. That doc's own next step was blocked: the fixture only attached
// console-errors.txt/weavie-host.log/viewport-layout.json on a *test-body* failure, code placed after
// `use(host)` — a setup-time throw skips it entirely, so this run landed with no diagnostic datum. Fixed the
// fixture (try/finally around setup+use) so the next occurrence captures that datum instead of widening the
// timeout or retrying, both of which the doc explicitly rules out.
test("create, switch, unload, and reopen sessions @cross", async ({ page }) => {
  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(1);
  const initialSlot = await activeSessionSlot(page);

  // Create: forking spins up a second session on its own worktree, which becomes active.
  await createSession(page, { branch: "e2e/session-lifecycle", provider: "claude" });
  await expect(chips).toHaveCount(2);
  await waitForSessionSwitch(page, initialSlot);

  // Switch: clicking the first chip makes it active.
  await chips.first().click();
  await expect(chips.first()).toHaveClass(/\bactive\b/);

  // Switch via the keyboard (Next Session) — still exactly one active session.
  const beforeNext = await activeSessionSlot(page);
  await runCommand(page, "Next Session");
  await waitForSessionSwitch(page, beforeNext);

  // Unload: the active session's backend is torn down; its chip goes faded/unloaded.
  await runCommand(page, "Unload Session");
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(1);
  await expect(
    page.locator(".toast", { hasText: "was unloaded. Its worktree was kept." }),
  ).toHaveCount(1);
  await expect(page.locator(".toast", { hasText: "Unloading the session" })).toHaveCount(0);

  // Reopen: clicking an unloaded chip loads it again (no longer unloaded).
  await page.locator(".session-chip.unloaded").click();
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);
});

test("the prompt-free action opens an existing branch", async ({ page }) => {
  const initialSlot = await activeSessionSlot(page);
  await runCommand(page, "Sessions");
  const inbox = page.locator(".session-inbox");
  const openGroup = inbox.getByRole("region", { name: "Open an existing branch" });
  const branch = await inbox.locator(".session-inbox-row.active strong").innerText();

  await inbox
    .getByRole("textbox", { name: "Prompt for a new session" })
    .fill("This draft must not become existing-session input");
  await openGroup.getByRole("combobox", { name: "Existing branch for the session" }).fill(branch);
  await openGroup.getByRole("button", { name: "Open", exact: true }).click();

  await expect(inbox).toBeHidden();
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    initialSlot,
  );
});

test("reload restores the client-selected stable session slot @cross", async ({ page, weavie }) => {
  const initialSlot = await activeSessionSlot(page);
  await createSession(page, { branch: "e2e/session-reload", provider: "claude" });
  await expect(page.locator(".session-chip")).toHaveCount(2);
  const slot = await waitForSessionSwitch(page, initialSlot);
  await expect
    .poll(async () => {
      const json = await readFile(join(weavie.home, ".weavie", "rail-state.json"), "utf8").catch(
        () => null,
      );
      if (json === null) {
        return null;
      }
      const state = JSON.parse(json) as { selected?: unknown };
      return state.selected;
    })
    .toEqual({ backendId: "local", slot });

  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });

  await expect(page.locator(".session-chip.active")).toHaveAttribute("data-session-slot", slot);
});

// Delete a (clean) session: right-click its chip → Delete… → confirm. A freshly forked worktree has no
// changes, so the confirm dialog is the plain clean-state variant (single danger button, no checkbox).
test("delete a session removes its chip @cross", async ({ page }) => {
  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(1);
  await createSession(page, { branch: "e2e/session-delete", provider: "claude" });
  await expect(chips).toHaveCount(2);

  await chips.nth(1).click({ button: "right" });
  await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();

  const dialog = page.locator(".confirm-dialog");
  await expect(dialog).toBeVisible();
  await dialog.locator(".confirm-btn-danger").click();

  await expect(chips).toHaveCount(1);
  await expect(
    page.locator(".toast", { hasText: "was deleted. Its branch was kept." }),
  ).toHaveCount(1);
  await expect(page.locator(".toast", { hasText: "Deleting session" })).toHaveCount(0);
});

test("delete confirmation names tracked and untracked work that will be lost @cross", async ({
  page,
  weavie,
}) => {
  const chips = page.locator(".session-chip");
  await createSession(page, { branch: "e2e/session-dirty-delete", provider: "claude" });
  await expect(chips).toHaveCount(2);
  const [worktree] = sessionWorktrees(weavie.workspace);
  if (worktree === undefined) {
    throw new Error("forked session did not create a git worktree");
  }
  await Promise.all([
    writeFile(join(worktree, "hello.ts"), "tracked edit\n"),
    writeFile(join(worktree, "scratch.txt"), "untracked work\n"),
  ]);

  await chips.nth(1).click({ button: "right" });
  await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();

  const dialog = page.locator(".confirm-dialog");
  await expect(dialog.locator(".confirm-file-list")).toContainText("hello.ts");
  await expect(dialog.locator(".confirm-file-list")).toContainText("scratch.txt");
  await expect(dialog.locator(".confirm-check input")).not.toBeChecked();
  await expect(dialog.locator(".confirm-btn-danger")).toBeDisabled();
});

test("deleting the workspace session keeps its checkout and creates a replacement", async ({
  page,
  weavie,
}) => {
  const chips = page.locator(".session-chip");
  const deletedId = await activeSessionSlot(page);

  await chips.first().click({ button: "right" });
  await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();

  const dialog = page.locator(".confirm-dialog");
  await expect(dialog).toContainText("Its checkout and files remain on disk.");
  await expect(dialog).not.toContainText("Remove the worktree");
  await dialog.locator(".confirm-btn-danger").click();

  await expect(chips).toHaveCount(1);
  await expect(chips.first()).not.toHaveAttribute("data-session-slot", deletedId);
  await expect(page.locator(".toast", { hasText: "was deleted." })).toHaveCount(1);
  expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).toContain("greet");
});
