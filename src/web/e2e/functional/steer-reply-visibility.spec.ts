import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("current thinking is one compact latest-summary status and clears when the turn ends", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "thought-status");
  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"font.size","value":28}');
  await expect(surface.locator(".agent-entry-result")).toContainText("setSetting:");
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(workspace, "hold-thought-delta"), "");
  await writeFile(join(workspace, "hold-thought-finish"), "");
  await submitAcpDraft(surface, "thought-disclosures");
  const status = surface.locator(".agent-current-thought");
  await expect(status).toHaveCount(1);
  await expect(status).toContainText("Checking narrow panes and streaming content");
  await expect(surface.locator(".agent-entry-thought")).toHaveCount(0);
  await expect(surface.locator(".agent-transcript .agent-current-thought")).toHaveCount(0);
  expect(
    await status.evaluate((element) => element.getBoundingClientRect().height),
  ).toBeLessThanOrEqual(32);

  await page.setViewportSize({ width: 390, height: 800 });
  await page.locator(".session-inbox-row.active .session-inbox-open").click();
  await expect(status).toBeVisible();
  const widths = await status.evaluate((element) => ({
    width: element.clientWidth,
    content: element.scrollWidth,
  }));
  expect(widths.width).toBeLessThanOrEqual(390);
  expect(widths.content).toBeLessThanOrEqual(widths.width);
  expect(
    await status.evaluate((element) => element.getBoundingClientRect().height),
  ).toBeLessThanOrEqual(32);

  await writeFile(join(workspace, "release-thought-delta"), "");
  await expect(status).toContainText("Checking stream continuity");
  await expect(status).not.toContainText("Checking narrow panes");
  await expect(status).toHaveAttribute("title", /The streamed continuation remains readable/);
  await expect(surface.locator(".agent-entry-thought")).toHaveCount(0);
  await writeFile(join(workspace, "release-thought-finish"), "");
  await expect(surface.locator(".agent-entry-result").last()).toContainText(
    "Thought disclosure checks complete.",
  );
  await expect(status).toHaveCount(0);
  await page.reload();
  await expect(surface.locator(".agent-entry-result").last()).toContainText(
    "Thought disclosure checks complete.",
  );
  await expect(status).toHaveCount(0);
  await expect(surface.locator(".agent-entry-thought")).toHaveCount(0);
});

test("all and collapsed retain complete thought history with ordinary prose typography", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "thought-history");
  await surface.locator(".pane-fullscreen").click();
  await submitAcpDraft(surface, "thought-disclosures");
  await expect(surface.locator(".agent-entry-result")).toContainText(
    "Thought disclosure checks complete.",
  );
  await expect(surface.locator(".agent-current-thought, .agent-entry-thought")).toHaveCount(0);
  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"all"}');
  const thoughts = surface.locator(".agent-entry-thought");
  await expect(thoughts).toHaveCount(2);
  await expect(thoughts.locator("summary")).toHaveCount(0);
  await expect(thoughts.first()).toContainText("The first thought stays available for inspection.");
  await expect(thoughts.last()).toContainText("The streamed continuation remains readable.");
  const typography = await thoughts.evaluateAll((entries) =>
    entries.flatMap((entry) => {
      const body = getComputedStyle(entry.querySelector(".agent-markdown")!);
      return [...entry.querySelectorAll("h1, h2, h3, h4, strong, em")].map((element) => {
        const style = getComputedStyle(element);
        return {
          size: style.fontSize,
          weight: style.fontWeight,
          bodySize: body.fontSize,
          bodyWeight: body.fontWeight,
        };
      });
    }),
  );
  expect(typography).toHaveLength(3);
  for (const style of typography) {
    expect(style.size).toBe(style.bodySize);
    expect(style.weight).toBe(style.bodyWeight);
  }
  await submitAcpDraft(
    surface,
    'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"collapsed"}',
  );
  await expect(thoughts.locator("details")).toHaveCount(2);
  await expect(thoughts.locator("details[open]")).toHaveCount(0);
  await thoughts.last().locator("summary").click();
  await expect(thoughts.last()).toContainText("The streamed continuation remains readable.");
  await expect(thoughts.last().locator("pre")).toContainText("y".repeat(240));
  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"current"}');
  await expect(thoughts).toHaveCount(0);
  await expect(surface.locator(".agent-current-thought")).toHaveCount(0);
  await page.reload();
  await expect(surface.locator(".agent-entry-result").last()).toContainText("setSetting:");
  await expect(surface.locator(".agent-current-thought, .agent-entry-thought")).toHaveCount(0);
});

test("parent and side-conversation thought statuses have independent lifetimes", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "thought-scopes");
  await surface.locator(".pane-fullscreen").click();
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(workspace, "hold-thought-delta"), "");
  await writeFile(join(workspace, "hold-steer-reply"), "");
  await writeFile(join(workspace, "hold-steer-finish"), "");
  await submitAcpDraft(surface, "/btw thought-disclosures");
  const aside = surface.locator(".agent-aside");
  const sideStatus = aside.locator(".agent-current-thought");
  const rootStatus = surface.locator(":scope > .agent-current-thought");
  await expect(sideStatus).toContainText("Checking narrow panes and streaming content");
  await expect(rootStatus).toHaveCount(0);
  await submitAcpDraft(surface, "steer-visibility-message");
  await expect(rootStatus).toContainText("Inspecting the build before your question.");
  await expect(sideStatus).toContainText("Checking narrow panes and streaming content");
  await submitAcpDraft(surface, "What agent is doing the building?");
  await expect(rootStatus).toHaveCount(0);
  await expect(sideStatus).toBeVisible();
  await writeFile(join(workspace, "release-steer-reply"), "");
  await expect(rootStatus).toContainText("Checking the build result.");
  await expect(sideStatus).toContainText("Checking narrow panes and streaming content");
  await writeFile(join(workspace, "release-steer-finish"), "");
  await expect(surface.locator(".agent-entry-result")).toContainText("Build verified.");
  await expect(rootStatus).toHaveCount(0);
  await expect(sideStatus).toBeVisible();
  await writeFile(join(workspace, "release-thought-delta"), "");
  await expect(aside.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "Thought disclosure checks complete.",
  );
  await expect(sideStatus).toHaveCount(0);
  await expect(surface.locator(".agent-entry-thought")).toHaveCount(0);
});

for (const mode of ["all", "current", "collapsed"]) {
  test(`assistant steering replies remain visible with ${mode} thinking`, async ({
    page,
    weavie,
  }) => {
    const surface = await createAcpSession(page, `steer-message-${mode}`);
    await surface.locator(".pane-fullscreen").click();
    await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.progressDisplay","value":"all"}');
    await expect(surface.locator(".agent-entry-result")).toContainText("setSetting:");
    await submitAcpDraft(
      surface,
      `mcp:setSetting:{"key":"agent.thoughtDisplay","value":"${mode}"}`,
    );
    await expect(surface.locator(".agent-entry-result").last()).toContainText("setSetting:");
    const workspace = sessionWorktrees(weavie.workspace)[0]!;
    await writeFile(join(workspace, "hold-steer-reply"), "");
    await writeFile(join(workspace, "hold-steer-finish"), "");
    await submitAcpDraft(surface, "steer-visibility-message");
    await expect(surface).toContainText("Inspecting the build before your question.");
    await submitAcpDraft(surface, "What agent is doing the building?");
    // The steer shows once the agent has accepted it; a reply released earlier can land before it.
    await expect(
      surface.locator(".agent-entry-message.agent-tone-user", {
        hasText: "What agent is doing the building?",
      }),
    ).toBeVisible();
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
