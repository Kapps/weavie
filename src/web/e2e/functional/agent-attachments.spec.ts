import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

test("highlighting reply text attaches it, and the agent receives it with the prompt", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "attach-selection");
  await submitAcpDraft(surface, "alpha bravo charlie");
  const reply = surface.locator(".agent-markdown", { hasText: "echo: alpha bravo charlie" });
  await expect(reply).toBeVisible();

  const box = (await reply.locator("p").first().boundingBox())!;
  await page.mouse.move(box.x + 1, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width - 1, box.y + box.height / 2, { steps: 8 });
  await page.mouse.up();

  const chip = surface.locator("[data-agent-composer] .agent-attachment-selection");
  await expect(chip).toHaveCount(1);
  await expect(chip).toContainText("echo: alpha bravo charlie");

  await submitAcpDraft(surface, "why?");

  await expect(
    surface.locator(".agent-markdown", {
      hasText: "Attached selection (echo: alpha bravo charlie):",
    }),
  ).toBeVisible();
  await expect(chip).toHaveCount(0);
  await expect(surface.locator(".agent-entry", { hasText: "Selection" }).last()).toContainText(
    "echo: alpha bravo charlie",
  );
});
