import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("current thoughts survive tools and completion, then group at the next user exchange", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "steer-replies");
  await surface.locator(".pane-fullscreen").click();
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(workspace, "hold-steer-reply"), "");
  await writeFile(join(workspace, "hold-steer-finish"), "");
  await submitAcpDraft(surface, "steer-visibility");
  const thoughts = surface.locator(".agent-entry-thought");
  const history = surface.locator(".agent-entry-thought-history");
  await expect(thoughts).toContainText(["Inspecting the build before your question."]);
  await expect(thoughts.locator("details")).toHaveCount(0);
  await submitAcpDraft(surface, "What agent is doing the building?");
  await expect(history).toHaveCount(1);
  await expect(history.locator("details[open]")).toHaveCount(0);
  await expect(
    surface.locator(".agent-entry-message", { hasText: "What agent is doing the building?" }),
  ).toBeVisible();
  await writeFile(join(workspace, "release-steer-reply"), "");
  await expect(thoughts).toHaveCount(2);
  const reply = thoughts.filter({ hasText: "I’m running the build directly in this session." });
  await expect(reply.locator(".agent-markdown")).toBeVisible();
  await expect(thoughts.last()).toContainText("Checking the build result.");
  await expect(surface.locator(".agent-entry-activity")).toHaveCount(2);
  await expect(surface.locator(".agent-activity-details[open]")).toHaveCount(0);
  await expect
    .poll(() =>
      surface.locator("article.agent-entry").evaluateAll((entries) =>
        entries.map((entry) => {
          if (entry.classList.contains("agent-entry-thought-history")) return "History";
          if (entry.classList.contains("agent-entry-thought")) return "Thinking";
          return entry.querySelector(".agent-entry-label")?.textContent?.trim();
        }),
      ),
    )
    .toEqual(["Prompt", "Working", "History", "Steer", "Thinking", "Working", "Thinking"]);
  await expect(surface.locator(".agent-entry-result")).toHaveCount(0);

  await writeFile(join(workspace, "release-steer-finish"), "");
  await expect(surface.locator(".agent-entry-result")).toContainText("Build verified.");
  await expect(reply.locator(".agent-markdown")).toBeVisible();
  await page.reload();
  await expect(reply.locator(".agent-markdown")).toBeVisible();
  await expect(thoughts).toHaveCount(2);
  await expect(history).toHaveCount(1);

  await submitAcpDraft(surface, "Continue after the build");
  await expect(surface.locator(".agent-entry-result").last()).toContainText(
    "echo: Continue after the build",
  );
  await expect(thoughts).toHaveCount(0);
  await expect(history).toHaveCount(2);
  await history.last().locator("summary").click();
  await expect(history.last()).toContainText("I’m running the build directly in this session.");
  await expect(history.last()).toContainText("Checking the build result.");
  await expect(history.last().locator(".agent-entry-activity, .agent-entry-result")).toHaveCount(0);
  await expect(surface.locator(".agent-entry-activity")).toHaveCount(2);
  await expect(surface.locator(".agent-entry-result").first()).toContainText("Build verified.");
  await history.last().locator("summary").press("Enter");
  await expect(history.last().locator("details[open]")).toHaveCount(0);

  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"all"}');
  await surface.locator(".agent-transcript-scroller").press("Home");
  await expect(history).toHaveCount(0);
  await expect(thoughts).toHaveCount(3);
  await expect(thoughts.locator("details")).toHaveCount(0);
  await expect(thoughts.first()).toContainText("Inspecting the build before your question.");
  await expect(reply.locator(".agent-markdown")).toBeVisible();
  await submitAcpDraft(
    surface,
    'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"collapsed"}',
  );
  await surface.locator(".agent-transcript-scroller").press("Home");
  await expect(thoughts.locator("details")).toHaveCount(3);
  await expect(thoughts.locator("details[open]")).toHaveCount(0);
  await thoughts.nth(1).locator("summary").click();
  await expect(reply.locator(".agent-markdown")).toBeVisible();
  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"current"}');
  await surface.locator(".agent-transcript-scroller").press("Home");
  await expect(history).toHaveCount(2);
  await expect(history.locator("details[open]")).toHaveCount(0);
  await page.reload();
  await surface.locator(".agent-transcript-scroller").press("Home");
  await expect(history).toHaveCount(2);
  await expect(history.locator("details[open]")).toHaveCount(0);
});

test("thought headings and emphasis read as ordinary prose and historical groups retain all text", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "thought-prose");
  await surface.locator(".pane-fullscreen").click();
  await submitAcpDraft(surface, "thought-disclosures");
  const thoughts = surface.locator(".agent-entry-thought");
  await expect(surface.locator(".agent-entry-result")).toContainText(
    "Thought disclosure checks complete.",
  );
  await expect(thoughts).toHaveCount(2);
  await expect(thoughts.locator("summary")).toHaveCount(0);
  const typography = await thoughts.evaluateAll((entries) =>
    entries.flatMap((entry) => {
      const body = getComputedStyle(entry.querySelector(".agent-markdown")!);
      return [...entry.querySelectorAll("h1, h2, h3, h4, strong, em")].map((element) => {
        const style = getComputedStyle(element);
        return {
          size: style.fontSize,
          weight: style.fontWeight,
          style: style.fontStyle,
          bodySize: body.fontSize,
          bodyWeight: body.fontWeight,
          bodyStyle: body.fontStyle,
        };
      });
    }),
  );
  expect(typography).toHaveLength(2);
  for (const style of typography) {
    expect(style.size).toBe(style.bodySize);
    expect(style.weight).toBe(style.bodyWeight);
    expect(style.style).toBe(style.bodyStyle);
  }

  await submitAcpDraft(surface, "Next exchange");
  const history = surface.locator(".agent-entry-thought-history");
  await expect(history).toHaveCount(1);
  await expect(history.locator("details[open]")).toHaveCount(0);
  await history.locator("summary").click();
  await expect(history).toContainText("The first thought stays available for inspection.");
  await expect(history).toContainText("The streamed continuation remains readable.");
  await expect(history.locator("pre")).toContainText("y".repeat(240));
  await expect(surface.locator(".agent-entry-activity")).toHaveCount(1);
  await expect(surface.locator(".agent-entry-result").first()).toContainText(
    "Thought disclosure checks complete.",
  );
});

test("a new parent exchange does not fold the current thoughts in a side conversation", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "thought-scopes");
  await surface.locator(".pane-fullscreen").click();
  await submitAcpDraft(surface, "/btw thought-disclosures");
  const aside = surface.locator(".agent-aside");
  await expect(aside.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "Thought disclosure checks complete.",
  );
  await expect(aside.locator(".agent-entry-thought")).toHaveCount(2);
  await expect(aside.locator(".agent-entry-thought summary")).toHaveCount(0);
  await submitAcpDraft(surface, "thought-disclosures");
  await expect(surface.locator(".agent-entry-result")).toHaveCount(1);
  await submitAcpDraft(surface, "Next parent exchange");
  await expect(surface.locator(".agent-entry-thought-history")).toHaveCount(1);
  await expect(aside.locator(".agent-entry-thought-history")).toHaveCount(0);
  await expect(aside.locator(".agent-entry-thought")).toHaveCount(2);
  await expect(aside.locator(".agent-entry-thought summary")).toHaveCount(0);
  await expect(aside).toContainText("The first thought stays available for inspection.");
  await expect(aside).toContainText("The streamed continuation remains readable.");
});

for (const mode of ["all", "current", "collapsed"]) {
  test(`assistant steering replies remain visible with ${mode} thinking`, async ({
    page,
    weavie,
  }) => {
    const surface = await createAcpSession(page, `steer-message-${mode}`);
    await surface.locator(".pane-fullscreen").click();
    await submitAcpDraft(
      surface,
      `mcp:setSetting:{"key":"agent.thoughtDisplay","value":"${mode}"}`,
    );
    await expect(surface.locator(".agent-entry-result")).toContainText("setSetting:");
    const workspace = sessionWorktrees(weavie.workspace)[0]!;
    await writeFile(join(workspace, "hold-steer-reply"), "");
    await writeFile(join(workspace, "hold-steer-finish"), "");
    await submitAcpDraft(surface, "steer-visibility-message");
    await expect(surface).toContainText("Inspecting the build before your question.");
    await submitAcpDraft(surface, "What agent is doing the building?");
    await writeFile(join(workspace, "release-steer-reply"), "");
    const reply = surface.locator(".agent-entry-message.agent-tone-assistant", {
      hasText: "I’m running the build directly in this session.",
    });
    await expect(reply.locator(".agent-markdown")).toBeVisible();
    await expect(surface.locator(".agent-entry-activity")).toHaveCount(2);
    await writeFile(join(workspace, "release-steer-finish"), "");
    await expect(surface.locator(".agent-entry-result").last()).toContainText("Build verified.");
    await expect(reply.locator(".agent-markdown")).toBeVisible();
    await page.reload();
    await expect(reply.locator(".agent-markdown")).toBeVisible();
    await submitAcpDraft(surface, "Next exchange");
    await expect(surface.locator(".agent-entry-result").last()).toContainText(
      "echo: Next exchange",
    );
    await expect(reply.locator(".agent-markdown")).toBeVisible();
  });
}
