import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("thought-carried steering replies stay visible through tools, completion, settings, and reload", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "steer-replies");
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(workspace, "hold-steer-reply"), "");
  await writeFile(join(workspace, "hold-steer-finish"), "");
  await submitAcpDraft(surface, "steer-visibility");
  const thoughts = surface.locator(".agent-entry-thought");
  await expect(thoughts).toContainText(["Inspecting the build before your question."]);
  await submitAcpDraft(surface, "What agent is doing the building?");
  await expect(
    surface.locator(".agent-entry-message", { hasText: "What agent is doing the building?" }),
  ).toBeVisible();
  await writeFile(join(workspace, "release-steer-reply"), "");
  await expect(thoughts).toHaveCount(3);
  const thoughtIds = await thoughts.evaluateAll((entries) =>
    entries.map(
      (entry) => entry.closest<HTMLElement>("[data-transcript-entry]")!.dataset.transcriptEntry!,
    ),
  );
  const thoughtById = (id: string) =>
    surface.locator(`[data-transcript-entry=${JSON.stringify(id)}] .agent-entry-thought`);
  const reply = thoughtById(thoughtIds[1]!);
  await expect(reply).toContainText("I’m running the build directly in this session.");
  await expect(reply.locator(".agent-markdown")).toBeVisible();
  await expect(surface.locator(".agent-entry-activity")).toHaveCount(2);
  await expect(surface.locator(".agent-activity-details[open]")).toHaveCount(0);
  await expect
    .poll(() =>
      surface.locator("article.agent-entry").evaluateAll((entries) =>
        entries.map((entry) => {
          if (entry.classList.contains("agent-entry-thought")) return "Thinking";
          return entry.querySelector(".agent-entry-label")?.textContent?.trim();
        }),
      ),
    )
    .toEqual(["Prompt", "Working", "Thinking", "Steer", "Thinking", "Working", "Thinking"]);
  await expect(surface.locator(".agent-entry-result")).toHaveCount(0);

  await writeFile(join(workspace, "release-steer-finish"), "");
  await expect(surface.locator(".agent-entry-result")).toContainText("Build verified.");
  await expect(reply.locator(".agent-markdown")).toBeVisible();
  await page.reload();
  await expect(reply.locator(".agent-markdown")).toBeVisible();
  await expect(thoughts).toHaveCount(3);
  await expect(surface.locator(".agent-entry-result")).toContainText("Build verified.");
  await page.evaluate(() => {
    const observed = window as Window & { thoughtResizeErrors: string[] };
    observed.thoughtResizeErrors = [];
    window.addEventListener("error", (event) => {
      if (event.message.includes("ResizeObserver"))
        observed.thoughtResizeErrors.push(event.message);
    });
  });

  await submitAcpDraft(
    surface,
    'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"collapsed"}',
  );
  await expect(thoughts.locator("details[open]")).toHaveCount(0);
  await thoughtById(thoughtIds[0]!).scrollIntoViewIfNeeded();
  await reply.locator("summary").click();
  await expect(reply.locator(".agent-markdown")).toBeVisible();
  await expect(surface.locator(".agent-activity-details[open]")).toHaveCount(0);
  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"inline"}');
  await surface.locator(".agent-transcript-scroller").press("Home");
  for (const id of thoughtIds) {
    const thought = thoughtById(id);
    await thought.scrollIntoViewIfNeeded();
    await expect(thought.locator(".agent-markdown")).toBeVisible();
  }
  await page.evaluate(
    () =>
      new Promise<void>((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
      ),
  );
  expect(
    await page.evaluate(
      () => (window as Window & { thoughtResizeErrors: string[] }).thoughtResizeErrors,
    ),
  ).toEqual([]);
});
