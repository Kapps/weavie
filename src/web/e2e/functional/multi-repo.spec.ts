import { execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { basename } from "node:path";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { createInfraRepo } from "../harness/git-workspace";

// One workspace holding sessions from two repositories: a session is started in a second repo from the composer,
// every chip then names its repo, and the labels go once the workspace is back to one repo. The host side of each
// repo-scoped behavior (setup command, test profile, pull requests, restore) is covered by HostCoreMultiRepoTests.

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
    // The workspace's own repo is what the list shows before anything is picked.
    await expect(repository).toHaveValue(weavie.workspace);
    const start = inbox.getByRole("button", { name: "Start", exact: true });
    const path = inbox.getByRole("textbox", { name: "Repository path" }).first();
    await expect(start).toBeEnabled();

    // "Other…" with no path names no repo; the list stays, to go back to one it offers.
    await repository.selectOption({ label: "Other…" });
    await expect(path).toBeFocused();
    await expect(start).toBeDisabled();
    await repository.selectOption({ label: basename(weavie.workspace) });
    await expect(path).toBeHidden();
    await expect(start).toBeEnabled();

    // Enter in the path starts the session in the repo it names, without waiting for that repo's workflows.
    await repository.selectOption({ label: "Other…" });
    await path.fill(infra);
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

    // "From current session" branches from the session the composer was opened from, in that session's repo.
    await inbox.getByRole("combobox", { name: "Branch starting point" }).selectOption("source");
    await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill("e2e/rollout");
    await inbox.getByRole("button", { name: "Start", exact: true }).click();
    await expect(inbox).toBeHidden();
    await expect(
      page.locator('.session-chip.active[title^="infra / e2e/rollout —"]'),
    ).toBeVisible();
    const parent = execFileSync("git", ["config", "--get-regexp", "^branch\\.e2e/rollout\\."], {
      cwd: infra,
      encoding: "utf8",
    });
    expect(parent).toContain("e2e/deploy");
  });

  await test.step("deleting the other repo's sessions leaves one repo and no labels", async () => {
    for (const remaining of [2, 1]) {
      await chips.nth(remaining).click({ button: "right" });
      await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();
      await page.locator(".confirm-dialog .confirm-btn-danger").click();
      await expect(chips).toHaveCount(remaining);
    }
    await expect(repoLabels).toHaveCount(0);
  });

  await test.step("the composer still offers the repo it has no session in", async () => {
    await page.locator(".session-rail-add").click();
    await expect(
      page
        .locator(".session-inbox")
        .getByRole("combobox", { name: "Repository", exact: true })
        .locator("option"),
    ).toHaveText([basename(weavie.workspace), "infra", "Other…"]);
  });
});

// The home folder is not Weavie's to remove: deleting its session drops the session only, and it stays gone
// until Open Home Folder Session brings it back.
test("the home folder's session is deleted with its folder kept, and reopened by command", async ({
  page,
  weavie,
}) => {
  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(1);

  await chips.first().click({ button: "right" });
  await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();
  await expect(page.locator(".confirm-dialog")).toContainText(
    "Its folder and everything in it are kept.",
  );
  await page.locator(".confirm-dialog .confirm-btn-danger").click();
  await expect(chips).toHaveCount(0);
  expect(existsSync(weavie.workspace)).toBe(true);

  await page.reload();
  await expect(page.locator("#splash")).toBeHidden();
  await expect(chips).toHaveCount(0);

  await runCommand(page, "Open Home Folder Session");
  await expect(chips).toHaveCount(1);
});
