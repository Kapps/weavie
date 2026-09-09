import { createAcpSession } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

test.describe("ACP plan review with automatic tool approval", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "agent.allowAllPermissions", value: true } },
      ],
    },
  });

  for (const scenario of [
    { prompt: "plan", choice: "Yes, implement this plan", result: "allow" },
    { prompt: "plan-existing", choice: "No, revise this plan", result: "reject" },
  ]) {
    test(`plan review waits for ${scenario.result} and accepts its terminal update`, async ({
      page,
    }) => {
      const surface = await createAcpSession(page, `acp-plan-${scenario.result}`);
      const composer = surface.locator("[data-agent-composer] textarea");

      await composer.fill("permission-lifecycle:immediate");
      await composer.press("Enter");
      await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
        "permission lifecycle: allow",
      );
      await expect(
        surface.locator(".agent-entry-request .agent-approval-actions button"),
      ).toHaveCount(0);

      await composer.fill(`permission-lifecycle:${scenario.prompt}`);
      await composer.press("Enter");
      const approval = surface.locator(".agent-entry-request");
      await expect(approval).toContainText("Implement this plan?");
      const implement = approval.getByRole("button", { name: "Yes, implement this plan" });
      const revise = approval.getByRole("button", { name: "No, revise this plan" });
      await expect(implement).toBeInViewport({ ratio: 1 });
      await expect(revise).toBeInViewport({ ratio: 1 });
      const payload = approval.locator(".agent-entry-text");
      await expect
        .poll(() => payload.evaluate((element) => element.scrollHeight > element.clientHeight))
        .toBe(true);
      await payload.hover();
      await page.mouse.wheel(0, 6500);
      await expect
        .poll(() =>
          payload.evaluate(
            (element) => element.scrollTop + element.clientHeight >= element.scrollHeight - 1,
          ),
        )
        .toBe(true);
      await expect(implement).toBeInViewport({ ratio: 1 });
      await expect(revise).toBeInViewport({ ratio: 1 });

      await surface.getByRole("button", { name: "Open plan" }).click();
      const document = page.locator(".editor-plan .agent-markdown");
      await expect(document).toContainText("Detailed work plan");
      await expect(document).toContainText("Implementation step 12");
      await expect(document).toContainText("End of the complete work plan.");
      await expect(implement).toBeInViewport({ ratio: 1 });
      await expect(revise).toBeInViewport({ ratio: 1 });

      await approval.getByRole("button", { name: scenario.choice }).click();
      await expect(
        surface.locator(".agent-entry-message.agent-tone-assistant").last(),
      ).toContainText(`permission lifecycle: ${scenario.result}`);
      await expect(
        surface.locator(".agent-entry-request .agent-approval-actions button"),
      ).toHaveCount(0);
      await expect(surface.locator(".agent-working")).toHaveCount(0);
      await expect(surface.locator(".agent-entry-notice.agent-tone-error")).toHaveCount(0);
      await expect(surface).not.toContainText("unknown tool call");
    });
  }
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
