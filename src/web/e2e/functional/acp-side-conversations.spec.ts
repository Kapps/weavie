import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot, runCommand, waitForSessionSwitch } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { pastePng } from "../harness/pasted-image";
import { collectTranscriptRows, revealTranscriptTarget } from "../harness/transcript-navigation";

for (const { primaryRunning, prompt } of [
  { primaryRunning: false, prompt: "image" },
  { primaryRunning: true, prompt: "image" },
  { primaryRunning: false, prompt: "" },
]) {
  test(`BTW sends ${prompt ? "pasted images" : "an image-only prompt"} ${primaryRunning ? "while the primary runs" : "before the primary starts"}`, async ({
    page,
  }) => {
    const surface = await createAcpSession(page, `acp-side-image-${primaryRunning}`);
    const composer = surface.locator("[data-agent-composer] textarea");
    if (primaryRunning) {
      await submitAcpDraft(surface, "hold");
      await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");
    }

    const png = await page.evaluate(() => {
      const canvas = document.createElement("canvas");
      canvas.width = 64;
      canvas.height = 64;
      const context = canvas.getContext("2d");
      if (context === null) throw new Error("Canvas is unavailable");
      context.fillStyle = "#e35d37";
      context.fillRect(0, 0, canvas.width, canvas.height);
      return canvas.toDataURL("image/png").split(",")[1];
    });
    await pastePng(composer, png);
    await expect(surface.locator(".agent-attachment")).toHaveAttribute("title", "ready");
    await submitAcpDraft(surface, `/btw ${prompt}`);

    const aside = surface.locator(".agent-aside");
    await expect(aside).toContainText(prompt ? "image=True" : "echo:");
    const image = aside.locator(".agent-entry-media");
    await expect(image).toBeVisible();
    await expect(image).toHaveJSProperty("naturalWidth", 64);
    await expect(image).toHaveAttribute("src", `data:image/png;base64,${png}`);
    await expect(surface.locator(".agent-attachment")).toHaveCount(0);
    await expect(composer).toHaveValue("");

    if (primaryRunning) {
      await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");
      await submitAcpDraft(surface, "finish primary independently");
      await expect(surface).toContainText("steered: finish primary independently");
      await expect(surface.locator(".agent-working")).toHaveCount(0);
    }
    await submitAcpDraft(surface, "image");
    await expect(surface).toContainText("image=False");
    await revealTranscriptTarget(surface, aside);
    await expect(aside).toContainText(prompt ? "image=True" : "echo:");
    await expect(aside).not.toContainText("image=False");
    await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
  });
}

test("BTW stays at its creation point through later primary output, side replies, and reopening", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-side-position");
  const composer = surface.locator("[data-agent-composer] textarea");
  await submitAcpDraft(surface, "hold");
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");
  await submitAcpDraft(surface, "/btw explain the side question");
  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText("echo: explain the side question");
  const conversationId = await aside.getAttribute("data-agent-aside");
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");

  await submitAcpDraft(surface, "primary continues after BTW");
  await expect(surface).toContainText("steered: primary continues after BTW");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await revealTranscriptTarget(surface, aside);
  const asideEntryId = await aside.evaluate((element) =>
    element.closest(".agent-virtual-row")!.getAttribute("data-transcript-entry"),
  );
  const expectCreationOrder = async (): Promise<void> => {
    const rows = await collectTranscriptRows(
      surface,
      ".agent-aside, .agent-entry-message .agent-entry-main",
    );
    expect(
      rows.flatMap((row) => {
        if (row.entryId === asideEntryId) return ["BTW"];
        if (row.texts.some((text) => text.includes("steered: primary continues after BTW")))
          return ["later primary output"];
        return [];
      }),
    ).toEqual(["BTW", "later primary output"]);
  };
  await expectCreationOrder();

  await revealTranscriptTarget(surface, aside.getByRole("button", { name: "Reply", exact: true }));
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = aside.getByRole("textbox", { name: "Reply to BTW" });
  await reply.fill("one more side detail");
  await reply.press("Enter");
  await expect(aside).toContainText("echo: one more side detail");
  await expectCreationOrder();

  await runCommand(page, "Unload Session");
  const unloaded = page.locator('.session-chip.unloaded[title^="acp-side-position"]');
  await expect(unloaded).toBeVisible();
  await unloaded.click();
  await revealTranscriptTarget(surface, aside);
  await expect(aside).toHaveAttribute("data-agent-aside", conversationId!);
  await expect(aside).toContainText("echo: one more side detail");
  await expectCreationOrder();
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
});

test("multiple BTW threads overlap the primary and route independent replies", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-concurrent-sides");
  const composer = surface.locator("[data-agent-composer] textarea");
  await submitAcpDraft(surface, "hold");
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");

  await submitAcpDraft(surface, "/btw input");
  const firstCreated = surface.locator(".agent-aside").first();
  await expect(firstCreated).toContainText("Choose a value");
  const firstId = await firstCreated.getAttribute("data-agent-aside");
  const first = surface.locator(`[data-agent-aside=${JSON.stringify(firstId)}]`);
  await submitAcpDraft(surface, "/btw input");
  const secondCreated = surface.locator(".agent-aside").nth(1);
  await expect(secondCreated).toContainText("Choose a value");
  const secondId = await secondCreated.getAttribute("data-agent-aside");
  const second = surface.locator(`[data-agent-aside=${JSON.stringify(secondId)}]`);
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");

  await revealTranscriptTarget(surface, second.getByRole("radio", { name: "Two", exact: false }));
  await second.getByRole("radio", { name: "Two", exact: false }).check();
  await revealTranscriptTarget(
    surface,
    second.getByRole("button", { name: "Submit answers", exact: true }),
  );
  await second.getByRole("button", { name: "Submit answers", exact: true }).click();
  await expect(second).toContainText("input: two");
  await expect(first.getByRole("button", { name: "Submit answers", exact: true })).toBeVisible();
  await revealTranscriptTarget(surface, second.getByRole("button", { name: "Reply", exact: true }));
  await second.getByRole("button", { name: "Reply", exact: true }).click();
  const secondReply = second.getByRole("textbox", { name: "Reply to BTW" });
  await secondReply.fill("identify-session");
  await secondReply.press("Enter");
  await expect(second).toContainText("session: fake-fork-fake-session-3");

  await revealTranscriptTarget(surface, first.getByRole("radio", { name: "One", exact: false }));
  await first.getByRole("radio", { name: "One", exact: false }).check();
  await revealTranscriptTarget(
    surface,
    first.getByRole("button", { name: "Submit answers", exact: true }),
  );
  await first.getByRole("button", { name: "Submit answers", exact: true }).click();
  await expect(first).toContainText("input: one");
  await expect(first).not.toContainText("input: two");
  await revealTranscriptTarget(surface, second);
  await expect(second).not.toContainText("input: one");
  await revealTranscriptTarget(surface, first.getByRole("button", { name: "Reply", exact: true }));
  await first.getByRole("button", { name: "Reply", exact: true }).click();
  const firstReply = first.getByRole("textbox", { name: "Reply to BTW" });
  await firstReply.fill("identify-session");
  await firstReply.press("Enter");
  await expect(first).toContainText("session: fake-fork-fake-session-2");

  await submitAcpDraft(surface, "finish primary independently");
  await revealTranscriptTarget(
    surface,
    surface.getByText("steered: finish primary independently", { exact: true }),
  );
  await expect(surface).toContainText("steered: finish primary independently");
  await revealTranscriptTarget(surface, first);
  await expect(first).not.toContainText("steered:");
  await revealTranscriptTarget(surface, second);
  await expect(second).not.toContainText("steered:");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
});

test("BTW collapse and nested history expansion preserve per-thread state across session switches", async ({
  page,
}) => {
  const initialSlot = await activeSessionSlot(page);
  const surface = await createAcpSession(page, "acp-side-history");
  const acpSlot = await activeSessionSlot(page);
  await submitAcpDraft(surface, "/btw rich");
  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText("rich response");
  const activity = aside.locator(".agent-entry-activity").first();
  await activity.locator("summary").click();
  const progress = activity.locator(".agent-activity-step", { hasText: "progress Task list" });
  await revealTranscriptTarget(surface, progress.getByText("show output", { exact: true }));
  await progress.getByText("show output", { exact: true }).click();
  await expect(progress.locator(".agent-tool-output")).toContainText("Inspect");
  await revealTranscriptTarget(surface, aside.getByRole("button", { name: "Reply", exact: true }));
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = aside.getByRole("textbox", { name: "Reply to BTW" });
  await reply.fill("draft belongs to this thread");

  await revealTranscriptTarget(
    surface,
    aside.getByRole("button", { name: "Collapse BTW", exact: true }),
  );
  await aside.getByRole("button", { name: "Collapse BTW", exact: true }).click();
  await expect(reply).toBeHidden();
  await expect(progress).toBeHidden();
  await aside.getByRole("button", { name: "Expand BTW", exact: true }).click();
  await expect(reply).toHaveValue("draft belongs to this thread");
  await expect(progress.locator(".agent-tool-output")).toContainText("Inspect");
  await reply.focus();
  await reply.press("Alt+b");
  await expect(aside.getByRole("button", { name: "Expand BTW", exact: true })).toBeVisible();
  await page.locator(`.session-chip[data-session-slot="${initialSlot}"]`).click();
  await waitForSessionSwitch(page, acpSlot);
  await page.locator(`.session-chip[data-session-slot="${acpSlot}"]`).click();
  await waitForSessionSwitch(page, initialSlot);
  await expect(aside.getByRole("button", { name: "Expand BTW", exact: true })).toBeVisible();
  await aside.getByRole("button", { name: "Expand BTW", exact: true }).click();
  await expect(reply).toHaveValue("draft belongs to this thread");
  await expect(activity.locator("details").first()).toHaveAttribute("open", "");
  await revealTranscriptTarget(surface, progress.getByText("show output", { exact: true }));
  await progress.getByText("show output", { exact: true }).click();
  await expect(progress.locator(".agent-tool-output")).toContainText("Inspect");

  await reply.fill("agent-terminal");
  await reply.press("Enter");
  await expect(aside).toContainText("agent terminal finished");
  const commandActivity = aside.locator(".agent-entry-activity").last();
  await revealTranscriptTarget(surface, commandActivity.locator("summary"));
  await commandActivity.locator("summary").click();
  const command = commandActivity.locator(".agent-activity-step", { hasText: "echo hello" });
  await revealTranscriptTarget(surface, command.getByText("show output", { exact: true }));
  await command.getByText("show output", { exact: true }).click();
  await expect(command.locator(".agent-tool-output")).toContainText("hello");
  await revealTranscriptTarget(surface, progress.locator(".agent-tool-output"));
  await expect(progress.locator(".agent-tool-output")).toContainText("Inspect");

  await revealTranscriptTarget(surface, aside.getByRole("button", { name: "Reply", exact: true }));
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  await reply.fill("draft survives transcript virtualization");
  const newerOutput = Array.from({ length: 80 }, (_, index) => `Unmount proof ${index}`).join("\n");
  await submitAcpDraft(surface, newerOutput);
  const viewport = surface.locator(".agent-body > .monaco-list");
  await viewport.focus();
  await viewport.press("End");
  await expect(surface).toContainText("echo: Unmount proof 0");
  await expect(aside).toHaveCount(0);
  await revealTranscriptTarget(surface, aside);
  await expect(reply).toHaveValue("draft survives transcript virtualization");
  await expect(activity.locator("details").first()).toHaveAttribute("open", "");
  await expect(commandActivity.locator("details").first()).toHaveAttribute("open", "");
});
