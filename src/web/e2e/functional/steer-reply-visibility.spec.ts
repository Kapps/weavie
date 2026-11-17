import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("all and collapsed retain complete thought history with ordinary prose typography", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "thought-history");
  await surface.locator(".pane-fullscreen").click();
  await submitAcpDraft(surface, "thought-disclosures");
  await expect(surface.locator(".agent-entry-result")).toContainText(
    "Thought disclosure checks complete.",
  );
  const thoughts = surface.locator(".agent-entry-thought");
  await expect(thoughts.locator("details")).toHaveCount(2);
  await expect(thoughts.locator("details[open]")).toHaveCount(0);
  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"all"}');
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
  await page.reload();
  await expect(surface.locator(".agent-entry-result").last()).toContainText("setSetting:");
  await expect(thoughts.locator("details")).toHaveCount(2);
});

for (const mode of ["all", "collapsed"]) {
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
