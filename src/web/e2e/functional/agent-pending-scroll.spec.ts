import { createAcpSession } from "../harness/acp-session";
import { activeSessionSlot, waitForSessionSwitch } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("pending input scrolls out of view while its answers survive reading history and session switches", async ({
  page,
}) => {
  const initialSlot = await activeSessionSlot(page);
  const surface = await createAcpSession(page, "pending-input-scroll");
  const acpSlot = await activeSessionSlot(page);
  await page.setViewportSize({ width: 900, height: 600 });
  const composer = surface.locator("[data-agent-composer] textarea");
  const body = surface.locator(".agent-body");
  const explanation = Array.from(
    { length: 18 },
    (_, index) =>
      `## Finding ${index + 1}\n\nThe earlier text stays available while the agent waits for an answer. Scroll up to read this explanation, then return to the pending questions.`,
  ).join("\n\n");
  await composer.fill(explanation);
  await composer.press("Enter");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "Finding 18",
  );
  await expect(surface.getByRole("button", { name: "Run", exact: true })).toBeVisible();
  await composer.fill("input-null-options");
  await composer.press("Enter");

  const request = body.locator(".agent-input-request");
  const answer = request.locator('input[type="text"]');
  const values = request.getByPlaceholder("One value per line");
  await answer.fill("Read the earlier explanation");
  await values.fill("keep this draft\nfinish after reviewing");
  await request.getByRole("button", { name: "Submit answers" }).hover();
  await page.mouse.wheel(0, -1_000);
  await expect(request).not.toBeInViewport();
  await expect(body.locator(".agent-entry-message.agent-tone-assistant")).toBeInViewport();
  await expect(values).toBeFocused();

  await page.locator(`.session-chip[data-session-slot="${initialSlot}"]`).click();
  await waitForSessionSwitch(page, acpSlot);
  await page.locator(`.session-chip[data-session-slot="${acpSlot}"]`).click();
  await waitForSessionSwitch(page, initialSlot);
  await expect(answer).toHaveValue("Read the earlier explanation");
  await expect(values).toHaveValue("keep this draft\nfinish after reviewing");

  await composer.focus();
  await composer.press("Alt+ArrowDown");
  await expect(request.getByRole("button", { name: "Submit answers" })).toBeInViewport();
  await request.getByRole("button", { name: "Submit answers" }).click();
  await expect(request).toHaveCount(0);
  const completion = body.locator(".agent-entry-message.agent-tone-assistant").last();
  await expect(completion).toContainText(
    "null options: Read the earlier explanation | keep this draft,finish after reviewing",
  );
  await expect(completion).toBeInViewport();
  await expect(surface.getByRole("button", { name: "Run", exact: true })).toBeVisible();
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
});
