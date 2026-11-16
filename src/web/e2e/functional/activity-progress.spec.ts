import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("earlier updates fold away while live work stays visible and tool failures stay in history", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "activity-progress");
  await surface.locator(".pane-fullscreen").click();
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(workspace, "hold-activity-progress"), "");
  await submitAcpDraft(surface, "activity-progress");
  const activities = surface.locator(".agent-entry-activity");
  const earlier = surface.getByRole("button", { name: /Earlier updates/ });
  const firstUpdate = surface.locator(".agent-entry-message", { hasText: "Update 1:" });
  await expect(earlier).toHaveAttribute("aria-expanded", "false");
  await expect(firstUpdate).toHaveCount(0);
  await expect(activities).toHaveCount(1);
  await expect(surface).toContainText("Checking the final verification result.");
  await expect(activities.last().locator("summary").first()).toContainText(
    "running search: Verify application",
  );
  await earlier.press("Enter");
  await expect(earlier).toHaveAttribute("aria-expanded", "true");
  await expect(activities).toHaveCount(7);
  await expect(activities.locator(".agent-entry-head")).toHaveCount(0);
  await expect(activities.locator("details[open]")).toHaveCount(0);
  const rows = await activities.evaluateAll((elements) =>
    elements.map((element) => {
      const summary = element.querySelector("summary")!;
      const style = getComputedStyle(element);
      return {
        height: element.getBoundingClientRect().height,
        summaryHeight: summary.getBoundingClientRect().height,
        border: style.borderTopWidth,
        background: style.backgroundColor,
      };
    }),
  );
  for (const row of rows) {
    expect(row.height).toBeLessThanOrEqual(32);
    expect(row.height).toBeLessThanOrEqual(row.summaryHeight + 8);
    expect(row.border).toBe("0px");
    expect(row.background).toBe("rgba(0, 0, 0, 0)");
  }
  for (let index = 1; index <= 6; index++) {
    await expect(
      surface.locator(".agent-entry-message", { hasText: `Update ${index}:` }),
    ).toBeInViewport();
  }

  await surface.locator(".pane-fullscreen").click();
  const runningSummary = activities.last().locator(".agent-activity-summary");
  await expect(runningSummary).toBeVisible();
  const narrow = await runningSummary.evaluate((element) => ({
    width: element.clientWidth,
    content: element.scrollWidth,
    overflow: getComputedStyle(element).textOverflow,
  }));
  expect(narrow.content).toBeGreaterThan(narrow.width);
  expect(narrow.overflow).toBe("ellipsis");
  await surface.locator(".pane-fullscreen").click();
  await earlier.click();
  await expect(firstUpdate).toHaveCount(0);
  await expect(activities).toHaveCount(1);

  await writeFile(join(workspace, "release-activity-progress"), "");
  const failed = activities.last();
  await expect(surface.locator(".agent-entry-result")).toContainText(
    "The verification needs attention",
  );
  await expect(activities).toHaveCount(0);
  await earlier.click();
  await expect(failed).not.toHaveClass(/agent-tone-error/);
  await expect(failed.locator("summary").first()).toHaveText("searched 1 time");
  await failed.locator("summary").first().focus();
  await page.keyboard.press("Enter");
  await expect(failed.locator(".agent-activity-step")).toContainText("Verify application");
  await expect(failed.locator(".agent-step-status")).toHaveText("failed");
  await failed.locator(".agent-tool-output-details > summary").press("Enter");
  await expect(failed.locator(".agent-tool-output")).toContainText(
    "Verification output: one assertion needs attention.",
  );

  await page.reload();
  await surface.locator(".pane-fullscreen").click();
  await expect(earlier).toHaveAttribute("aria-expanded", "false");
  await expect(firstUpdate).toHaveCount(0);
  await expect(activities).toHaveCount(0);
  await earlier.click();
  await expect(failed.locator("summary").first()).toHaveText("searched 1 time");
  await failed.locator("summary").first().click();
  await failed.locator(".agent-tool-output-details > summary").click();
  await expect(failed.locator(".agent-tool-output")).toContainText(
    "Verification output: one assertion needs attention.",
  );
  await earlier.click();

  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.progressDisplay","value":"all"}');
  await expect(surface.locator(".agent-entry-result").last()).toContainText("setSetting:");
  await expect(earlier).toHaveCount(0);
  await surface.locator(".agent-body").press("Home");
  await expect(firstUpdate).toBeInViewport();
  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.progressDisplay","value":"latest"}');
  await expect(earlier).toHaveAttribute("aria-expanded", "false");
  await expect(firstUpdate).toHaveCount(0);
  await expect(failed).toHaveCount(0);
});

test("replies the agent stopped on stay visible while later work joins the one group of earlier updates", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "reply-stops");
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(workspace, "hold-replies-wake"), "");
  await submitAcpDraft(surface, "replies-claude");
  const earlier = surface.getByRole("button", { name: /Earlier updates/ });
  const replies = surface.locator(".agent-entry-message.agent-tone-assistant");
  const answer = "Nothing is overridden, and the checks are running.";
  await expect(replies).toContainText([answer]);
  await expect(earlier).toHaveText("Earlier updates · 2");

  // The agent is woken inside the open turn, then stops three more times.
  await writeFile(join(workspace, "release-replies-wake"), "");
  const stops = [
    answer,
    "The review is clean.",
    "Publishing the change.",
    "Everything is published.",
  ];
  await expect(replies).toContainText(stops);
  await expect(earlier).toHaveText("Earlier updates · 6");

  // A background job wakes it after the turn completed.
  await writeFile(join(workspace, "release-replies-late"), "");
  const shown = [...stops, "The late job finished."];
  await expect(replies).toContainText(shown);
  await expect(earlier).toHaveText("Earlier updates · 7");

  await page.reload();
  await expect(replies).toContainText(shown);
  await expect(earlier).toHaveText("Earlier updates · 7");
  await earlier.click();
  for (const note of ["Reading the settings first.", "Checks pass; reading the review next."]) {
    await expect(replies.filter({ hasText: note })).toBeVisible();
  }
});

for (const side of [false, true]) {
  test(`expanding long earlier updates in ${side ? "BTW" : "main"} keeps their header and first text in view`, async ({
    page,
    weavie,
  }) => {
    const surface = await createAcpSession(page, "long-activity-progress");
    await surface.locator(".pane-fullscreen").click();
    const workspace = sessionWorktrees(weavie.workspace)[0]!;
    await writeFile(join(workspace, "hold-activity-progress"), "");
    await submitAcpDraft(surface, `${side ? "/btw " : ""}activity-progress-long`);
    const conversation = side ? surface.locator(".agent-aside") : surface;
    const earlier = conversation.getByRole("button", { name: "Earlier updates · 48" });
    await expect(earlier).toBeInViewport();
    await earlier.click();
    await expect(earlier).toHaveAttribute("aria-expanded", "true");
    await expect(earlier).toBeInViewport();
    await expect(
      conversation.locator(".agent-entry-message", { hasText: "Update 1:" }),
    ).toBeInViewport();
    await earlier.click();
    await expect(earlier).toHaveAttribute("aria-expanded", "false");
    await expect(earlier).toBeInViewport();
    await expect(conversation).toContainText("Checking the final verification result.");
    await writeFile(join(workspace, "release-activity-progress"), "");
  });
}
