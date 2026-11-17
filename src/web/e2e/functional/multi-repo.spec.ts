import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { basename, dirname, join } from "node:path";
import { expect, test } from "../harness/fixtures";

// One workspace holding sessions from two repositories: a session is started in a second repo from the composer,
// every chip then names its repo, and the labels go once the workspace is back to one repo. The host side of each
// repo-scoped behavior (setup command, test profile, pull requests, restore) is covered by HostCoreMultiRepoTests.

// A second repository beside the workspace, with one commit so a branch can fork from it.
function createInfraRepo(workspace: string): string {
  const dir = join(dirname(workspace), "infra");
  mkdirSync(dir);
  writeFileSync(join(dir, "deploy.yaml"), "region: us-east-1\n");
  const git = (...args: string[]) => execFileSync("git", args, { cwd: dir, stdio: "ignore" });
  git("init", "-q", "-b", "main");
  git("config", "user.email", "e2e@example.com");
  git("config", "user.name", "Weavie E2E");
  git("add", "-A");
  git("commit", "-q", "-m", "seed");
  return dir;
}

test("a session in a second repo joins the rail, labelled by repo until it is deleted", async ({
  page,
  weavie,
}) => {
  const infra = createInfraRepo(weavie.workspace);
  const chips = page.locator(".session-chip");
  const repoLabels = page.locator(".session-chip .session-chip-repo");
  await expect(chips).toHaveCount(1);
  await expect(repoLabels).toHaveCount(0);

  await test.step("start a session in the other repo by its path", async () => {
    await page.locator(".session-rail-add").click();
    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("claude");
    await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill("e2e/deploy");
    const repository = inbox.getByRole("combobox", { name: "Repository", exact: true });
    await expect(repository.locator("option")).toHaveText([basename(weavie.workspace), "Other…"]);
    const start = inbox.getByRole("button", { name: "Start", exact: true });
    const path = inbox.getByRole("textbox", { name: "Repository path" }).first();
    await expect(start).toBeEnabled();

    // "Other…" with no path names no repo; the list stays, to go back to one it offers.
    await repository.selectOption({ label: "Other…" });
    await expect(path).toBeVisible();
    await expect(start).toBeDisabled();
    await repository.selectOption({ label: basename(weavie.workspace) });
    await expect(path).toBeHidden();
    await expect(start).toBeEnabled();

    // Enter in the path starts the session in the repo it names.
    await repository.selectOption({ label: "Other…" });
    await path.fill(infra);
    await expect(start).toBeEnabled();
    await path.press("Enter");
    await expect(inbox).toBeHidden();
  });

  await test.step("both sessions name their repo, and the worktree is the other repo's", async () => {
    await expect(chips).toHaveCount(2);
    await expect(repoLabels).toHaveText([basename(weavie.workspace), "infra"]);
    await expect(page.locator('.session-chip.active[title^="infra / e2e/deploy —"]')).toBeVisible();
    const branches = (cwd: string): string =>
      execFileSync("git", ["branch", "--list", "e2e/deploy"], { cwd, encoding: "utf8" }).trim();
    expect(branches(infra)).toContain("e2e/deploy");
    expect(branches(weavie.workspace)).toBe("");
  });

  await test.step("the composer now offers the repo by name", async () => {
    await page.locator(".session-rail-add").click();
    const inbox = page.locator(".session-inbox");
    const repository = inbox.getByRole("combobox", { name: "Repository", exact: true });
    await expect(repository.locator("option")).toHaveText([
      basename(weavie.workspace),
      "infra",
      "Other…",
    ]);
    await expect(repository).toHaveValue(infra);
    await page.keyboard.press("Escape");
    await expect(inbox).toBeHidden();
  });

  await test.step("deleting it leaves one repo and no labels", async () => {
    await chips.nth(1).click({ button: "right" });
    await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();
    await page.locator(".confirm-dialog .confirm-btn-danger").click();
    await expect(chips).toHaveCount(1);
    await expect(repoLabels).toHaveCount(0);
  });
});
