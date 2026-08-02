import { expect, test } from "../harness/fixtures";

test.use({
  fakeScript: {
    steps: [{ op: "hook", request: { hook_event_name: "SessionStart", source: "startup" } }],
  },
});

test("compact session inbox creates, resumes, and switches existing surfaces", async ({ page }) => {
  const inbox = page.locator(".session-inbox");
  await expect(inbox).toBeVisible();
  await expect(inbox.locator(".session-inbox-row")).toHaveCount(1);

  await inbox
    .getByRole("textbox", { name: "Prompt for a new session" })
    .fill("Improve mobile navigation");
  await inbox.getByRole("button", { name: "Start" }).click();

  await expect(inbox).toBeHidden();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  await expect(page.locator(".terminal-surface[data-kind='terminal:claude']")).toBeVisible();

  await page.getByRole("button", { name: "Shell" }).click();
  await expect(page.locator(".terminal-surface[data-kind='terminal:shell']")).toBeVisible();

  await page.getByRole("button", { name: "Code" }).click();
  await expect(page.locator(".editor-surface")).toBeVisible();
  await page.getByRole("button", { name: "Files" }).click();
  await page.locator(".browser-row", { hasText: "hello.ts" }).click();
  await page.getByTitle("Close (Esc)").click();
  await expect(page.locator(".monaco-editor")).toBeVisible();

  await page.getByRole("button", { name: "Sessions" }).click();
  await expect(inbox.locator(".session-inbox-row")).toHaveCount(2);
  await expect(inbox).toContainText("improve-mobile-navigation");
  await inbox.locator(".session-inbox-row").first().click();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");

  const bar = page.locator(".mobile-surface-bar");
  await bar.dispatchEvent("pointerdown", { clientX: 300, pointerType: "touch" });
  await bar.dispatchEvent("pointerup", { clientX: 120, pointerType: "touch" });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Shell");

  const agentSurface = page.locator(".terminal-surface[data-kind='terminal:claude']");
  await agentSurface.evaluate((element) => {
    (window as Window & { __breakpointProof?: Element }).__breakpointProof = element;
  });
  await page.setViewportSize({ width: 900, height: 844 });
  await expect(page.locator(".session-rail")).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("button", { name: "Agent" }).click();
  expect(
    await agentSurface.evaluate(
      (element) =>
        (window as Window & { __breakpointProof?: Element }).__breakpointProof === element,
    ),
  ).toBe(true);
});
