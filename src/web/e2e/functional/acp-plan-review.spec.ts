import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { clickIntoEditor, pressDocumentEnd, typeInEditor } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test.describe("ACP plan review with automatic tool approval", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "agent.allowAllPermissions", value: true } },
      ],
    },
  });

  test("Claude's plan file opens for editing and an unsaved edit is accepted with the keyboard @cross", async ({
    page,
  }) => {
    const surface = await createAcpSession(page, "acp-plan-file");
    const composer = surface.locator("[data-agent-composer] textarea");
    await composer.fill("exit-plan-mode");
    await composer.press("Enter");

    await expect(page.locator(".editor-tab.active")).toContainText("fake-plan.md");
    await expect(composer).toBeFocused();
    const card = surface.locator(".agent-entry-request");
    await expect(card).toContainText("Ready to review in the editor");
    await expect(card.getByRole("button")).toHaveText(["Open plan"]);
    const bar = page.locator(".plan-review-bar");
    const accept = bar.getByRole("button", { name: /Yes, manually approve edits/ });
    await expect(accept).toHaveAttribute("title", /\((Ctrl|⌘)\+Enter\)/);

    await page.reload({ waitUntil: "domcontentloaded" });
    await expect(accept).toBeVisible();
    await clickIntoEditor(page);
    await pressDocumentEnd(page);
    await typeInEditor(page, "Edited in the editor.");
    await page.keyboard.press("ControlOrMeta+Enter");

    await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
      "Edited in the editor.",
    );
    await expect(bar).toHaveCount(0);
    await expect(surface.locator(".agent-entry-notice.agent-tone-error")).toHaveCount(0);
  });

  test("a plan sent as text is a read-only review that can keep planning", async ({ page }) => {
    const surface = await createAcpSession(page, "acp-plan-text");
    const composer = surface.locator("[data-agent-composer] textarea");
    await composer.fill("permission-lifecycle:plan");
    await composer.press("Enter");

    const document = page.locator(".editor-plan .agent-markdown");
    await expect(document).toContainText("End of the complete work plan.");
    await expect(composer).toBeFocused();
    await expect(surface.getByRole("button", { name: "No, revise this plan" })).toHaveCount(0);
    await page
      .locator(".plan-review-bar")
      .getByRole("button", { name: "No, revise this plan" })
      .click();

    await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
      "permission lifecycle: reject",
    );
    await expect(page.locator(".plan-review-bar")).toHaveCount(0);
    await surface.getByRole("button", { name: "Open plan" }).first().click();
    await expect(document).toContainText("End of the complete work plan.");
  });

  test("a side conversation's plan review answers only that conversation", async ({ page }) => {
    const surface = await createAcpSession(page, "acp-plan-btw");
    await submitAcpDraft(surface, "/btw exit-plan-mode");
    await expect(page.locator(".editor-tab.active")).toContainText("fake-plan.md");
    await page
      .locator(".plan-review-bar")
      .getByRole("button", { name: /Yes, manually approve edits/ })
      .click();
    await expect(surface.locator(".agent-aside")).toContainText("approved plan:");
    await expect(page.locator(".plan-review-bar")).toHaveCount(0);
    await expect(surface.locator(".agent-entry-notice.agent-tone-error")).toHaveCount(0);
  });
});

test("an open ACP plan shows full revisions and remains removed after reconnect", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-live-plan");
  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.fill("full-plan");
  await composer.press("Enter");
  await surface.getByRole("button", { name: "Open plan" }).click();

  const document = page.locator(".editor-plan .agent-markdown");
  await expect(document).toContainText("Detailed work plan");
  await expect(document.locator("h2", { hasText: /^Implementation step \d+$/ })).toHaveCount(12);
  await expect(document.locator("pre")).toContainText("request → review → implement");
  await expect(document).toContainText("End of the complete work plan.");

  await composer.fill("full-plan-revision");
  await composer.press("Enter");
  await expect(document).toContainText("Preserve the complete revised document.");
  await expect(document).toContainText("End of the complete work plan.");
  await expect(document.locator("h2", { hasText: /^Implementation step \d+$/ })).toHaveCount(12);
  await expect(composer).toBeFocused();
  await expect(page.locator(".editor-tab", { hasText: "Plan" })).toHaveCount(1);

  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(document).toContainText("Preserve the complete revised document.");
  await expect(document).toContainText("End of the complete work plan.");

  await composer.fill("remove-plan");
  await composer.press("Enter");
  const unavailable = page.locator(".editor-plan-notice");
  await expect(unavailable).toHaveText("This plan is no longer available.");
  await expect(document).toHaveCount(0);
  await expect(composer).toBeFocused();

  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(unavailable).toHaveText("This plan is no longer available.");
  await expect(document).toHaveCount(0);
  await expect(surface.getByRole("button", { name: "Open plan" })).toHaveCount(0);
  await expect(surface.locator(".agent-entry-notice.agent-tone-error")).toHaveCount(0);
});
