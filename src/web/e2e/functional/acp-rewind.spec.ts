import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

test("Rewind continues from before a prompt and puts it back in the composer", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-rewind");
  const composer = surface.locator("[data-agent-composer] textarea");
  for (const prompt of ["alpha", "bravo", "charlie"]) {
    await submitAcpDraft(surface, prompt);
    await expect(surface).toContainText(`echo: ${prompt}`);
  }
  const prompt = (text: string) =>
    surface.locator(".agent-entry-message.agent-tone-user", { hasText: text });
  await expect(prompt("charlie").locator(".agent-entry-rewind")).toHaveAttribute(
    "title",
    "Rewind to here",
  );

  await prompt("bravo").hover();
  await prompt("bravo").locator(".agent-entry-rewind").click();

  await expect(composer).toHaveValue("bravo");
  await expect(surface).toContainText("echo: alpha");
  await expect(surface).not.toContainText("echo: bravo");
  await expect(surface).not.toContainText("charlie");
  await submitAcpDraft(surface, "delta");
  await expect(surface).toContainText("echo: delta");

  await submitAcpDraft(surface, "/rewind");
  await expect(composer).toHaveValue("delta");
  await expect(surface).not.toContainText("echo: delta");
  await expect(surface).toContainText("echo: alpha");
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
});
