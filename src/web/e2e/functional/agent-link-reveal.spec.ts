import { awaitEditorReady, createSession, expectRevealed } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The structured agent pane's file links, full stack: a reference the agent printed is clicked in the
// transcript and the editor actually opens that file at that line. The mock-host spec
// (agent-markdown-links.spec.ts) stops at the published `reveal` and answers it with a scripted open, so
// nothing pinned the rest of the journey — click → reveal → FileOpener → open-file → Monaco. Every reference
// shape agents print is covered, because only the one WITH folders used to work: a bare filename and an
// authored `[text](file.ts:3)` link were both misread as a `file.ts:` URI scheme and silently dropped.

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
