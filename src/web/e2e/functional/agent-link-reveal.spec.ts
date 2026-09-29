import { awaitEditorReady, createSession, expectRevealed } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Agent links exercise real reference resolution through to Monaco, including bare paths, line ranges and
// authored Markdown links. The mock-host suite separately controls the resolution reply and pane focus.

test("transcript file references open the file at their line", async ({ page }) => {
  await awaitEditorReady(page);
  await createSession(page, { branch: "agent-links", provider: "fake-acp" });
  const surface = page.locator('[data-surface="structured-agent"]');

  // The fake agent echoes the prompt back as an assistant message, so the transcript quotes the references
  // exactly as an agent would print them.
  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.click();
  await composer.fill("`hello.ts:3`, long.ts:42, `long.ts:96-120`, [the rest](long.ts:130)");
  await composer.press("Enter");
  const message = surface.locator(".agent-entry-message.agent-tone-assistant").last();
  await expect(message).toContainText("long.ts:42");

  // A bare filename: no folders before the `:line`, so it must not be read as a `hello.ts:` scheme.
  await message.locator("code a", { hasText: "hello.ts:3" }).click();
  await expectRevealed(page, "hello.ts", 3);

  // The same shape unquoted, in prose.
  await message.locator("a", { hasText: "long.ts:42" }).click();
  await expectRevealed(page, "long.ts", 42);

  // A line range links as one reference and reveals its first line.
  await message.locator("code a", { hasText: "long.ts:96-120" }).click();
  await expectRevealed(page, "long.ts", 96);

  // A link the agent authored in markdown, whose href is the path — it has to survive the renderer's
  // safe-link policy as well as activation.
  await message.locator("a", { hasText: "the rest" }).click();
  await expectRevealed(page, "long.ts", 130);

  // The user's own echoed message renders through the plain-text linkifier, not the markdown one.
  await surface
    .locator(".agent-entry-message.agent-tone-user")
    .last()
    .locator("a", { hasText: "long.ts:42" })
    .click();
  await expectRevealed(page, "long.ts", 42);
});
