import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("BTW messages its session's busy main conversation through messageSession", async ({
  page,
}) => {
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
  await expect(aside).toContainText("Delivered to the main conversation of your own session");
  const delivered = surface.locator(".agent-entry", {
    has: page.locator(".agent-entry-label", { hasText: "From this session" }),
  });
  await expect(delivered).toHaveCount(1);
  await expect(delivered).toContainText("finding from the side");
  await expect(aside.locator(".agent-entry-label", { hasText: "From this session" })).toHaveCount(
    0,
  );
  await expect(surface).toContainText("steered: finding from the side");
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
});
