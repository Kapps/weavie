import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot, expectRevealed, waitForSessionSwitch } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// The fresh-incarnation path is the invariant: no unload/reload is allowed between creation, control discovery,
// and the first submitted turn. Both transports exercise the real HostCore and generic ACP process seam.
test("new ACP session initializes and accepts its first prompt @cross", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-first-turn");
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await expect(surface.getByRole("button", { name: "Mode Default" })).toBeVisible();

  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.click();
  await submitAcpDraft(surface, "first turn works");

  await expect(surface.locator(".agent-entry.agent-tone-user")).toContainText("first turn works");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "echo: first turn works",
  );
});

test("ACP slash commands preserve provider command and fresh-conversation semantics", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-slash-commands");
  const composer = surface.locator("[data-agent-composer] textarea");

  await submitAcpDraft(surface, "identify-session");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "session: fake-session",
  );

  await composer.fill("/");
  const menu = surface.locator(".agent-slash-menu");
  await expect(menu).toContainText("/compact");
  await expect(menu).toContainText("/clear");

  await submitAcpDraft(surface, "/compact");
  const compact = surface.locator(".agent-entry-message.agent-tone-user", {
    hasText: "/compact",
  });
  await expect(compact.locator(".agent-entry-label")).toHaveText("Command");
  await expect(
    surface.locator(".agent-entry-message.agent-tone-assistant", {
      hasText: "Compacting completed.",
    }),
  ).toBeVisible();

  await composer.fill("/clear");
  await expect(menu).toBeVisible();
  await composer.press("Escape");
  await expect(menu).toBeHidden();
  await composer.press("Enter");
  await expect(surface.locator(".agent-entry")).toHaveCount(0);
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();

  await submitAcpDraft(surface, "identify-session");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "session: fake-session-2",
  );
  await expect(surface.locator(".agent-entry-message.agent-tone-user")).toContainText(
    "identify-session",
  );
});

test("ACP side replies survive session switches and run alongside the primary turn", async ({
  page,
}) => {
  const initialSlot = await activeSessionSlot(page);
  const surface = await createAcpSession(page, "acp-side-reply-draft");
  const acpSlot = await activeSessionSlot(page);
  const composer = surface.locator("[data-agent-composer] textarea");

  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();

  await submitAcpDraft(surface, "/btw Explain one detail aside");
  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText("echo: Explain one detail aside");
  await expect(surface.locator(".agent-working")).toBeVisible();
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = aside.getByRole("textbox", { name: "Reply to BTW" });
  await reply.fill("unfinished follow-up");

  await page.locator(`.session-chip[data-session-slot="${initialSlot}"]`).click();
  await waitForSessionSwitch(page, acpSlot);
  await page.locator(`.session-chip[data-session-slot="${acpSlot}"]`).click();
  await waitForSessionSwitch(page, initialSlot);

  await expect(reply).toBeVisible();
  await expect(reply).toHaveValue("unfinished follow-up");

  await reply.press("Enter");
  const followUp = aside.locator(".agent-entry-message.agent-tone-assistant", {
    hasText: "echo: unfinished follow-up",
  });
  await expect(followUp).toHaveCount(1);
  await expect(reply).toHaveCount(0);

  await page.locator(`.session-chip[data-session-slot="${initialSlot}"]`).click();
  await waitForSessionSwitch(page, acpSlot);
  await page.locator(`.session-chip[data-session-slot="${acpSlot}"]`).click();
  await waitForSessionSwitch(page, initialSlot);

  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(reply).toHaveValue("");
  await reply.fill("cancelled but recoverable");
  await aside.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(reply).toHaveCount(0);

  await page.locator(`.session-chip[data-session-slot="${initialSlot}"]`).click();
  await waitForSessionSwitch(page, acpSlot);
  await page.locator(`.session-chip[data-session-slot="${acpSlot}"]`).click();
  await waitForSessionSwitch(page, initialSlot);

  await expect(reply).toHaveCount(0);
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(reply).toHaveValue("cancelled but recoverable");
  await reply.fill("input-cancel");
  await reply.press("Enter");
  const cancel = aside.getByRole("button", { name: "Cancel", exact: true });
  await expect(cancel).toBeVisible();
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");

  await submitAcpDraft(surface, "finish while the side question waits");
  await expect(surface).toContainText("steered: finish while the side question waits");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await expect(composer).toHaveAttribute(
    "placeholder",
    "Write a prompt — / for commands and skills",
  );
  await expect(cancel).toBeVisible();
  await cancel.click();
  await expect(aside).toContainText("input action: cancel");
  await expect(aside.getByRole("button", { name: "Reply", exact: true })).toBeEnabled();
});

test("BTW continues on its fork and returns control to the primary conversation", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-shared-process");
  await submitAcpDraft(surface, "primary context");
  await expect(surface).toContainText("echo: primary context");

  await submitAcpDraft(surface, "/btw identify-session");
  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText("session: fake-fork-fake-session-2");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = aside.getByRole("textbox", { name: "Reply to BTW" });
  await reply.fill("follow-up stays in the side conversation");
  await reply.press("Enter");
  await expect(aside).toContainText("echo: follow-up stays in the side conversation");

  await submitAcpDraft(surface, "identify-session");
  await expect(
    surface.locator(".agent-entry-message.agent-tone-assistant", {
      hasText: "session: fake-session",
    }),
  ).toBeVisible();
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
});

for (const switchDuringStartup of [false, true]) {
  test(`ACP controls and rich structured output stay native${switchDuringStartup ? " across startup session switches" : ""} @cross`, async ({
    page,
    weavie,
  }) => {
    const initialSlot = await activeSessionSlot(page);
    const surface = await createAcpSession(page, "acp-rich-output");
    const acpSlot = await activeSessionSlot(page);
    await writeFile(
      join(sessionWorktrees(weavie.workspace)[0]!, "sample.txt"),
      "one\ntwo\nthree\nfour\nfive\nsix\nnew\n",
    );

    await surface.getByRole("button", { name: "Model Alpha" }).click();
    await surface.getByRole("option", { name: "Beta" }).click();
    await expect(surface.getByRole("button", { name: "Model Beta" })).toBeVisible();

    await surface.getByRole("button", { name: "Fast Off" }).click();
    await surface.getByRole("option", { name: "On" }).click();
    await expect(surface.getByRole("button", { name: "Fast On" })).toBeVisible();

    await surface.getByRole("button", { name: "Mode Default" }).click();
    await surface.getByRole("option", { name: "Plan" }).click();
    await expect(surface.getByRole("button", { name: "Mode Plan" })).toBeVisible();

    await submitAcpDraft(surface, "rich");

    await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
      "rich response",
    );
    const activity = surface.locator(".agent-entry-activity").last();
    await expect(activity).toContainText("edited 1 file");
    await activity.locator("summary").click();
    await expect(activity.getByRole("button", { name: "Review edit" })).toBeVisible();
    await expect(surface.locator(".agent-working")).toHaveCount(0);

    // The agent reported 123 of 4096 context tokens; the circle renders that share.
    const usage = surface.getByRole("button", { name: "Context window 3% used" });
    await expect(usage.locator(".agent-usage-circle")).toBeVisible();
    await usage.hover();
    const tooltip = page.getByRole("tooltip");
    // The grouping separator follows the browser locale; the token counts do not.
    await expect(tooltip).toContainText(/123 of 4.?096 tokens/);
    // The agent also reported a weekly window at 0.62 utilization through Claude's _meta extension.
    await expect(tooltip).toContainText("Weekly limit");
    await expect(tooltip).toContainText("62% used · approaching limit");

    const editorRequested = Promise.withResolvers<void>();
    const releaseEditor = Promise.withResolvers<void>();
    await page.route(/\/assets\/editor-host-[^/]+\.js$/, async (route) => {
      editorRequested.resolve();
      await releaseEditor.promise;
      await route.continue();
    });
    await page.reload({ waitUntil: "domcontentloaded" });
    await editorRequested.promise;
    await expect(surface).toContainText("rich response");
    await activity.locator("summary").click();
    const edit = activity.locator(".agent-activity-step", { hasText: "Edit file" });
    await expect(edit.getByText("show output", { exact: true })).toHaveCount(0);
    await edit.getByRole("button", { name: "Review edit" }).click();
    await expect(page.locator(".editor-tab", { hasText: "sample.txt" })).toBeVisible();
    await expect(page.locator(".editor")).not.toHaveAttribute("data-ready", "true");
    if (switchDuringStartup) {
      await page.locator(`.session-chip[data-session-slot="${initialSlot}"]`).click();
      await waitForSessionSwitch(page, acpSlot);
      await page.locator(`.session-chip[data-session-slot="${acpSlot}"]`).click();
      await waitForSessionSwitch(page, initialSlot);
    }
    releaseEditor.resolve();
    await expectRevealed(page, "sample.txt", 7);
    const progress = activity.locator(".agent-activity-step", { hasText: "progress Task list" });
    await progress.getByText("show output", { exact: true }).click();
    await expect(progress.locator(".agent-tool-output")).toContainText("Inspect");
  });
}

test("ACP task progress stays activity while plan documents remain openable", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-plan-distinction");

  await submitAcpDraft(surface, "rich");

  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "rich response",
  );
  const activity = surface.locator(".agent-entry-activity").last();
  await expect(activity).toContainText("1 progress");
  await expect(surface.locator(".agent-entry-plan")).toHaveCount(0);
  await expect(surface.getByRole("button", { name: "Open plan" })).toHaveCount(0);
  await activity.locator("summary").click();
  const progress = activity.locator(".agent-activity-step", { hasText: "progress Task list" });
  await expect(progress).not.toContainText("Inspect");
  await progress.getByText("show output", { exact: true }).click();
  await expect(progress.locator(".agent-tool-output")).toContainText("Inspect");

  await submitAcpDraft(surface, "plan-document");

  const plan = surface.locator(".agent-entry-plan");
  await expect(plan).toContainText("Ready to review in the editor");
  await plan.getByRole("button", { name: "Open plan" }).click();
  await expect(page.locator(".editor-plan")).toBeVisible();
  await expect(page.locator(".editor-plan .agent-markdown")).toContainText("Implementation plan");
});

test("a slash command submitted mid-turn queues without blocking steering", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-queued-command");

  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();

  await submitAcpDraft(surface, "/compact");
  await expect(surface.locator(".agent-compose-queued")).toContainText("/compact");

  await submitAcpDraft(surface, "steer past the queued command");
  await expect(
    surface.locator(".agent-entry-message.agent-tone-assistant", {
      hasText: "steered: steer past the queued command",
    }),
  ).toBeVisible();
  await expect(
    surface.locator(".agent-entry-message.agent-tone-assistant", {
      hasText: "Compacting completed.",
    }),
  ).toBeVisible();
  await expect(surface.locator(".agent-compose-queued")).toHaveCount(0);
});

test("ACP steering and background completion return the session to idle @cross", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-steering");
  const composer = surface.locator("[data-agent-composer] textarea");

  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");
  await submitAcpDraft(surface, "use the native pane");
  await expect(
    surface.locator(".agent-entry-message.agent-tone-user", { hasText: "Steer" }),
  ).toContainText("use the native pane");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "steered: use the native pane",
  );
  await expect(surface.locator(".agent-working")).toHaveCount(0);

  await submitAcpDraft(surface, "background");
  const subagentActivity = surface.locator(".agent-entry-activity", {
    hasText: "execute: Background agent",
  });
  await expect(subagentActivity).toContainText("running");
  await expect(surface.locator(".agent-working")).toBeVisible();
  await submitAcpDraft(surface, "finish-background");
  await expect(surface).toContainText("background finished");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await expect(composer).toHaveAttribute(
    "placeholder",
    "Write a prompt — / for commands and skills",
  );
});
