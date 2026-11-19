import { expect } from "@playwright/test";
import { agentTurns, test } from "./harness/agent-pane";

test("a side conversation spaces its turns like the pane and keeps its reply box clear of them", async ({
  openPanes,
}) => {
  const { body, host, page, session } = await openPanes({ "nested-spacing": agentTurns(1) });
  const compact = page.getByRole("button", { name: "Agent", exact: true });
  if (await compact.isVisible()) await compact.click();
  await expect(page.getByText("Answer 0", { exact: true })).toBeVisible();
  const side = {
    providerId: "acp",
    conversationId: "side",
    anchorTurnId: "h0",
    threadId: "side-thread",
    isPrimaryThread: false,
  };
  const turn = (turnId: string, prompt: string, reply: string): void => {
    host.publishAgentPane(session.address, { ...side, turnId, type: "turn-started" });
    host.publishAgentPane(session.address, {
      ...side,
      turnId,
      type: "user-message",
      itemId: `side-prompt-${turnId}`,
      text: prompt,
    });
    host.publishAgentPane(session.address, {
      ...side,
      turnId,
      type: "item-completed",
      itemId: `side-reply-${turnId}`,
      itemType: "agentMessage",
      status: "completed",
      text: reply,
    });
    host.publishAgentPane(session.address, {
      ...side,
      turnId,
      type: "turn-completed",
      status: "completed",
    });
  };
  host.publishAgentPane(session.address, {
    ...side,
    turnId: "1",
    type: "side-conversation-started",
    status: "forking",
    text: "First side question",
  });
  turn("1", "First side question", "First side answer");
  turn("2", "Second side question", "Second side answer");

  const aside = body.locator(".agent-aside");
  const entries = aside.locator(".agent-aside-transcript .agent-entry");
  await expect(entries).toHaveCount(4);
  const edges = await entries.evaluateAll((elements) =>
    elements.map((element) => {
      const { top, bottom } = element.getBoundingClientRect();
      return { top, bottom };
    }),
  );
  const gaps = edges.slice(1).map((edge, index) => Math.round(edge.top - edges[index]!.bottom));
  // A prompt keeps 9px above and 7px below it; a reply 5px above.
  expect(gaps).toEqual([12, 9, 12]);

  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = aside.getByRole("textbox", { name: "Reply to BTW" });
  await expect(reply).toBeFocused();
  const clearance = await reply.evaluate(
    (element) =>
      element.getBoundingClientRect().top -
      element.closest("div:has(> .agent-aside-reply)")!.getBoundingClientRect().top,
  );
  expect(clearance).toBeGreaterThanOrEqual(6);
  // Focus shows on the border, so the card's clipped edge cuts nothing off.
  await expect(reply).toHaveCSS("outline-style", "none");

  // A press on the box's padding stays in the reply, and the box's corner drags it taller.
  const box = aside.locator(".ui-draft");
  await reply.blur();
  await box.click({ position: { x: 30, y: 3 } });
  await expect(reply).toBeFocused();
  const { x, y, width, height } = (await box.boundingBox())!;
  await page.mouse.move(x + width - 3, y + height - 3);
  await page.mouse.down();
  await page.mouse.move(x + width - 3, y + height + 57);
  await page.mouse.up();
  expect((await box.boundingBox())!.height).toBeGreaterThan(height + 40);
});
