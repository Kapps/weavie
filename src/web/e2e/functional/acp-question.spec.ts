import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

const question =
  "Given that distinction, what should “Current” show? Actual message replies will stay visible in either case.";
const notice =
  "Codex keeps working while you answer. An answer sent after it finishes is not delivered.";
const second = "All thinking since my latest message, as in mockup C";

test("title-only ACP choices show their question and retain the answer through history reload", async ({
  page,
}, testInfo) => {
  const surface = await createAcpSession(page, "question-history");
  await runCommand(page, "Toggle Fullscreen Pane");
  await submitAcpDraft(surface, "input-question");
  const card = surface.locator(".agent-entry-question");
  await expect(card.locator(".agent-question-title")).toHaveText(question);
  await expect(card.getByText(notice, { exact: true })).toHaveCount(1);
  await expect(card.locator(".agent-question-description")).toHaveCount(0);
  for (const width of [1400, 390]) {
    await page.setViewportSize({ width, height: 850 });
    if (width === 390) await page.getByRole("button", { name: "Agent", exact: true }).click();
    for (const colorScheme of ["light", "dark"] as const) {
      await page.emulateMedia({ colorScheme });
      await expect(page.locator("html")).toHaveAttribute("data-theme-type", colorScheme);
      await expect(card).toBeVisible();
      const cardBounds = await card.boundingBox();
      const bodyBounds = await surface.locator(".agent-body").boundingBox();
      expect(cardBounds!.x).toBeGreaterThanOrEqual(bodyBounds!.x);
      expect(cardBounds!.x + cardBounds!.width).toBeLessThanOrEqual(
        bodyBounds!.x + bodyBounds!.width,
      );
      const fits = await card.locator(".agent-choice-row").evaluateAll((rows) =>
        rows.every((row) => {
          const label = row.querySelector(".agent-choice-label")!;
          const range = document.createRange();
          range.selectNodeContents(label);
          const text = range.getBoundingClientRect();
          const bounds = row.getBoundingClientRect();
          return (
            text.left >= bounds.left && text.right <= bounds.right && text.bottom <= bounds.bottom
          );
        }),
      );
      expect(fits).toBe(true);
      await card.screenshot({ path: testInfo.outputPath(`question-${width}-${colorScheme}.png`) });
    }
  }
  await page.setViewportSize({ width: 1400, height: 850 });
  await card.locator(".agent-choice-row", { hasText: second }).click();
  await expect(surface.locator(".agent-markdown").last()).toContainText(second);
  await surface.getByRole("button", { name: /Earlier updates/ }).click();
  await card.getByText("show prompt and options", { exact: true }).click();
  await expect(card.locator(".agent-question-title")).toHaveText(question);
  await expect(card.locator(".agent-input-choice.chosen")).toContainText(second);
  await expect(card.getByText(notice, { exact: true })).toHaveCount(1);

  await page.reload();
  await surface.getByRole("button", { name: /Earlier updates/ }).click();
  await expect(card.getByText("show prompt and options", { exact: true })).toBeVisible();
  await card.getByText("show prompt and options", { exact: true }).click();
  await expect(card.locator(".agent-question-title")).toHaveText(question);
  await expect(card.locator(".agent-input-choice.chosen")).toContainText(second);
  await expect(card.getByText(notice, { exact: true })).toHaveCount(1);
});

test("ACP custom answers submit by Send and Enter and choices use their rebound shortcut", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "question-answers");
  const pending = surface.locator(".agent-entry-question.agent-entry-waiting");
  for (const method of ["Send", "Enter"]) {
    await submitAcpDraft(surface, "input-question");
    const answer = `Keep both in a compact history via ${method}`;
    await pending.locator(".agent-choice-other").fill(answer);
    if (method === "Send") await pending.getByRole("button", { name: "Send", exact: true }).click();
    else await pending.locator(".agent-choice-other").press("Enter");
    await expect(surface.locator(".agent-markdown").last()).toContainText(answer);
    await expect(pending).toHaveCount(0);
  }

  await writeFile(
    join(weavie.home, ".weavie", "keybindings.json"),
    JSON.stringify([
      { key: "alt+2", command: "-weavie.agent.answerByIndex" },
      {
        key: "alt+shift+2",
        command: "weavie.agent.answerByIndex",
        args: { index: 2 },
        when: "agentFocused && agentChoicePending",
      },
    ]),
  );
  await submitAcpDraft(surface, "input-question");
  await expect(pending.locator(".agent-choice-row").nth(1)).toHaveAttribute(
    "title",
    /Alt\+Shift\+2/,
  );
  await surface.locator("[data-agent-composer] textarea").focus();
  await page.keyboard.press("Alt+Shift+2");
  await expect(surface.locator(".agent-markdown").last()).toContainText(second);
  await expect(pending).toHaveCount(0);
});
