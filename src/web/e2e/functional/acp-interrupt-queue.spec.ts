import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { collectTranscriptRows, revealTranscriptTarget } from "../harness/transcript-navigation";

test("interrupt preserves queued slash commands and runs them once in order", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-interrupt-queue");

  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();

  for (const command of ["/compact", "/review queued after interrupt"]) {
    await submitAcpDraft(surface, command);
    await expect(surface.locator(".agent-compose-queued")).toContainText(command);
  }
  await surface.getByRole("button", { name: "Interrupt", exact: true }).click();

  const responseSelector = ".agent-entry-message.agent-tone-assistant";
  const review = surface.locator(responseSelector, {
    hasText: "review command: queued after interrupt",
  });
  await expect(review).toBeVisible();
  await expect(surface.locator(".agent-compose-queued")).toHaveCount(0);
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  const expected = [
    "steered: cancelled",
    "Compacting completed.",
    "review command: queued after interrupt",
  ];
  const before = await collectTranscriptRows(surface, responseSelector);
  expect(before.flatMap((row) => row.texts)).toEqual(
    expected.map((text) => expect.stringContaining(text)),
  );

  await submitAcpDraft(surface, "fresh prompt after interrupt");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  const fresh = surface.locator(responseSelector, {
    hasText: "echo: fresh prompt after interrupt",
  });
  await revealTranscriptTarget(surface, fresh);
  await expect(fresh).toBeVisible();
  const after = await collectTranscriptRows(surface, responseSelector);
  expect(after.slice(0, before.length)).toEqual(before);
  expect(after.flatMap((row) => row.texts)).toEqual(
    [...expected, "echo: fresh prompt after interrupt"].map((text) =>
      expect.stringContaining(text),
    ),
  );
  await expect(surface.locator(".agent-entry-notice.agent-tone-error")).toHaveCount(0);
});
