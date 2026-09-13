import { createAcpSession } from "../harness/acp-session";
import { activeSessionSlot, openCommandPalette, waitForSessionSwitch } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("reporting commands prepare private drafts in the owning agent without sending", async ({
  page,
}) => {
  const original = await activeSessionSlot(page);
  const surface = await createAcpSession(page, "issue-reporting");
  const owner = await activeSessionSlot(page);
  const composer = surface.locator("[data-agent-composer] textarea");

  for (const [title, opening, key] of [
    ["Report a Weavie Bug", "Help me file a bug report about Weavie", "B"],
    ["Request a Weavie Feature", "Help me file a feature request for Weavie", "F"],
  ]) {
    await openCommandPalette(page);
    await page.locator(".tb-omnibar-input").fill(`>${title}`);
    const row = page.locator(".tb-omnibar-row").filter({ hasText: title });
    await expect(row).toHaveCount(1);
    await expect(row.locator(".tb-row-keys")).toContainText(key);
    await row.click();
    await expect(composer).toHaveValue(new RegExp(`^${opening}`));
    await expect(composer).toHaveValue(/Never include source code from non-public repositories/);
    await expect(composer).toHaveValue(/Treat unknown repository visibility as non-public/);
    await expect(composer).toHaveValue(
      /obtain approval of that concrete content before publishing/,
    );
    await expect(composer).toHaveValue(/Always target Kapps\/weavie explicitly/);
    await expect(surface.locator(".agent-entry")).toHaveCount(0);
  }

  const draft = await composer.inputValue();
  await page.locator(`.session-chip[data-session-slot="${original}"]`).click();
  await waitForSessionSwitch(page, owner);
  await page.locator(`.session-chip[data-session-slot="${owner}"]`).click();
  await waitForSessionSwitch(page, original);
  await expect(composer).toHaveValue(draft);
  await expect(surface.locator(".agent-entry")).toHaveCount(0);

  await composer.fill("");
  await page.keyboard.press("ControlOrMeta+Alt+b");
  await expect(composer).toHaveValue(/^Help me file a bug report about Weavie/);
  await expect(surface.locator(".agent-entry")).toHaveCount(0);
});
