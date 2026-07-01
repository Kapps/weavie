import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";

// Pins #139: the Go-to-File index is built from `git ls-files --cached --others --exclude-standard`, so a
// gitignored file (a secret, a build artifact) never surfaces in quick-open, while tracked and
// untracked-not-ignored files still do. Regression guard for GitService.ListWorkspaceFilesAsync +
// HostCore.GitTrackedFilesAsync — without the fix the old unfiltered walk surfaced the ignored file.
test("Go-to-File omits gitignored files but keeps tracked + untracked ones", async ({
  weavie,
  page,
}) => {
  const ws = weavie.workspace;
  // Ignore a folder via the working-tree .gitignore (read live by `git ls-files --exclude-standard`).
  writeFileSync(join(ws, ".gitignore"), "ignored-zone/\n");
  mkdirSync(join(ws, "ignored-zone"), { recursive: true });
  writeFileSync(join(ws, "ignored-zone", "secret-zqx139.txt"), "must not appear\n");
  // An untracked, NOT-ignored file in a normal area — proves new files still open (the --others arm).
  writeFileSync(join(ws, "tracked-probe-zqx139.ts"), "export const x = 1;\n");

  const input = page.locator(".tb-omnibar-input");
  const rows = page.locator(".tb-omnibar-list .tb-omnibar-row");

  // Focusing the omnibar fires request-file-index, so the freshly-written files are in the pushed index.
  await input.click();

  // The gitignored file must be absent — the search resolves to the empty state.
  await input.fill("secret-zqx139");
  await expect(page.locator(".tb-omnibar-empty")).toHaveText("No matching files");
  await expect(rows).toHaveCount(0);

  // The untracked-not-ignored file must be present.
  await input.fill("tracked-probe-zqx139");
  await expect(rows.filter({ hasText: "tracked-probe-zqx139.ts" })).toHaveCount(1);

  // A committed tracked file (the seed README) must still be present.
  await input.fill("README");
  await expect(rows.filter({ hasText: "README.md" }).first()).toBeVisible();
});
