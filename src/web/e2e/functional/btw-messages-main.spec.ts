import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("BTW and its session message each other through messageSession", async ({ page }) => {
  const surface = await createAcpSession(page, "btw-messages-main");
  const composer = surface.locator("[data-agent-composer] textarea");
  await submitAcpDraft(surface, "hold");
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");

  const id = await activeSessionSlot(page);
  await submitAcpDraft(
    surface,
    `/btw mcp:messageSession:${JSON.stringify({ id, message: "finding from the side" })}`,
  );

  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText(`Delivered to`);
  const fromBtw = page.locator(".agent-entry-label", { hasText: /^From .+ BTW$/ });
  const delivered = surface.locator(".agent-entry", { has: fromBtw });
  await expect(delivered).toHaveCount(1);
  await expect(delivered).toContainText("finding from the side");
  await expect(aside.locator(fromBtw)).toHaveCount(0);
  await expect(surface).toContainText("steered: finding from the side");

  await submitAcpDraft(surface, 'mcp:listSessions:{"includeBtw":true}');
  await expect(surface).toContainText(`${id}#`);
  const side = (await surface.innerText()).match(new RegExp(`${id}#[0-9a-f]{32}`))?.[0];
  await submitAcpDraft(
    surface,
    `mcp:messageSession:${JSON.stringify({ id: side, message: "answer from main" })}`,
  );

  // The transcript is virtualized and the card sits at the turn it was asked in.
  await surface
    .locator(".agent-transcript-scroller")
    .evaluate((el) => el.scrollTo(0, -el.scrollHeight));
  const answered = aside.locator(".agent-entry", {
    has: page.locator(".agent-entry-label", { hasText: /^From (?!.* BTW$)/ }),
  });
  await expect(answered).toContainText("answer from main");
  await expect(aside).toContainText("echo: answer from main");
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
});
