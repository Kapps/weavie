import { awaitEditorReady, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("View Logs includes startup phases and early web marks without configuration", async ({
  page,
  weavie,
}) => {
  await awaitEditorReady(page);
  await expect.poll(() => weavie.log()).toContain("[startup/web] editor-ready");
  await runCommand(page, "View Logs");
  await expect(page.locator(".editor-tab.active")).toContainText("Weavie Logs");
  const logs = page.locator(".editor-source pre");
  await expect(logs).toBeVisible();
  for (const phase of [
    "end backend startup",
    "end worktree discovery",
    "end restore sessions",
    "review restore and disk reconciliation",
    "backend ready",
    "[startup/web] module-eval",
    "[startup/web] shell-mounted",
    "[startup/web] splash-dismissed",
    "[startup/web] editor-ready",
  ]) {
    await expect(logs).toContainText(phase);
  }
});
