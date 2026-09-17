import { awaitEditorReady, runCommand } from "../harness/actions";
import { writeFakeScript } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import type { HeadlessHost } from "../harness/weavie-host";

test("startup timing is disabled by default", async ({ page, weavie }) => {
  await awaitEditorReady(page);
  await runCommand(page, "View Logs");
  await expect(page.locator(".editor-tab.active")).toContainText("Weavie Logs");
  const logs = page.locator(".editor-source pre");
  await expect(logs).toBeVisible();
  await expect(logs).not.toContainText("[startup/");
  expect(weavie.log()).not.toContain("[startup/");
});

test.describe("enabled startup timing", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "diagnostics.startupTiming", value: true } },
      ],
    },
  });

  test("View Logs includes backend phases and the earliest web marks after restart", async ({
    page,
    weavie,
  }) => {
    test.slow();
    await expect.poll(() => weavie.fakeLog()).toContain("setSetting ->");
    await writeFakeScript(weavie.home, []);
    await page.goto("about:blank");
    await (weavie as HeadlessHost).restart();
    const connect = await page.request.post(weavie.url, {
      form: { token: weavie.token },
      maxRedirects: 0,
    });
    expect(connect.status()).toBe(302);
    await page.goto(weavie.url);
    await expect(page.locator("#splash")).toHaveCount(0);
    await awaitEditorReady(page);
    await expect.poll(() => weavie.log()).toContain("[startup/web] editor-ready");
    await runCommand(page, "View Logs");
    await expect(page.locator(".editor-tab.active")).toContainText("Weavie Logs");
    const logs = page.locator(".editor-source pre");
    await expect(logs).toBeVisible();
    for (const phase of [
      "end backend startup (origin)",
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
});
