import { createAcpSession } from "../harness/acp-session";
import { activeSessionSlot, runCommand, waitForSessionSwitch } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { pastePng } from "../harness/pasted-image";

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
      await composer.fill("hold");
      await composer.press("Enter");
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
    await composer.fill(`/btw ${prompt}`);
    await composer.press("Enter");

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
      await composer.fill("finish primary independently");
      await composer.press("Enter");
      await expect(surface).toContainText("steered: finish primary independently");
      await expect(surface.locator(".agent-working")).toHaveCount(0);
    }
    await composer.fill("image");
    await composer.press("Enter");
    await expect(surface).toContainText("image=False");
    await expect(aside).not.toContainText("image=False");
    await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
  });
}

test("BTW stays at its creation point through later primary output, side replies, and reopening", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-side-position");
  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.fill("hold");
  await composer.press("Enter");
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");
  await composer.fill("/btw explain the side question");
  await composer.press("Enter");
  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText("echo: explain the side question");
  const conversationId = await aside.getAttribute("data-agent-aside");
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");

  await composer.fill("primary continues after BTW");
  await composer.press("Enter");
  await expect(surface).toContainText("steered: primary continues after BTW");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  const rows = surface.locator(".agent-transcript > .agent-virtual-row");
  const expectCreationOrder = async (): Promise<void> => {
    await expect
      .poll(() =>
        rows.evaluateAll((elements) =>
          elements.flatMap((element) => {
            if (element.querySelector(".agent-aside")) return ["BTW"];
            if (element.textContent?.includes("steered: primary continues after BTW")) {
              return ["later primary output"];
            }
            return [];
          }),
        ),
      )
      .toEqual(["BTW", "later primary output"]);
  };
  await expectCreationOrder();

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
  await expect(aside).toHaveAttribute("data-agent-aside", conversationId!);
  await expect(aside).toContainText("echo: one more side detail");
  await expectCreationOrder();
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
});

test("multiple BTW threads overlap the primary and route independent replies", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-concurrent-sides");
  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.fill("hold");
  await composer.press("Enter");
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");

  await composer.fill("/btw input");
  await composer.press("Enter");
  const first = surface.locator(".agent-aside").nth(0);
  await expect(first).toContainText("Choose a value");
  await composer.fill("/btw input");
  await composer.press("Enter");
  const second = surface.locator(".agent-aside").nth(1);
  await expect(second).toContainText("Choose a value");
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");

  await second.getByRole("radio", { name: "Two", exact: false }).check();
  await second.getByRole("button", { name: "Submit answers", exact: true }).click();
  await expect(second).toContainText("input: two");
  await expect(first.getByRole("button", { name: "Submit answers", exact: true })).toBeVisible();
  await second.getByRole("button", { name: "Reply", exact: true }).click();
  const secondReply = second.getByRole("textbox", { name: "Reply to BTW" });
  await secondReply.fill("identify-session");
  await secondReply.press("Enter");
  await expect(second).toContainText("session: fake-fork-fake-session-3");

  await first.getByRole("radio", { name: "One", exact: false }).check();
  await first.getByRole("button", { name: "Submit answers", exact: true }).click();
  await expect(first).toContainText("input: one");
  await expect(first).not.toContainText("input: two");
  await expect(second).not.toContainText("input: one");
  await first.getByRole("button", { name: "Reply", exact: true }).click();
  const firstReply = first.getByRole("textbox", { name: "Reply to BTW" });
  await firstReply.fill("identify-session");
  await firstReply.press("Enter");
  await expect(first).toContainText("session: fake-fork-fake-session-2");

  await composer.fill("finish primary independently");
  await composer.press("Enter");
  await expect(surface).toContainText("steered: finish primary independently");
  await expect(first).not.toContainText("steered:");
  await expect(second).not.toContainText("steered:");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
});

// Flaked on windows-latest 2026-09-09 06:21 UTC (run 34317635773, job 102358015410): `.agent-aside` never
// appeared within 30s — not a text mismatch, the element itself was never found. Root cause: `/btw rich` here
// is the very first composer action after `createAcpSession` returns, which races the ACP handshake
// (`_ready`/`_supportsFork`/`_supportsLoad`, set together once `session/new` resolves). AskAside threw
// InvalidOperationException on `!_ready` and HostCore.Sessions.cs silently swallowed it, so the aside panel
// never mounted — unlike an ordinary prompt sent in the same window, which Submit() safely queues and
// delivers once ready. Fixed at the root in AcpAgentSession.AskAside/FlushPendingAsides (queue instead of
// throwing on `!_ready`, flushed once the handshake completes) rather than in this test.
test("BTW collapse and nested history expansion preserve per-thread state across session switches", async ({
  page,
}) => {
  const initialSlot = await activeSessionSlot(page);
  const surface = await createAcpSession(page, "acp-side-history");
  const acpSlot = await activeSessionSlot(page);
  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.fill("/btw rich");
  await composer.press("Enter");
  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText("rich response");
  const activity = aside.locator(".agent-entry-activity").first();
  await activity.locator("summary").click();
  const progress = activity.locator(".agent-activity-step", { hasText: "progress Task list" });
  await progress.getByText("show output", { exact: true }).click();
  await expect(progress.locator(".agent-tool-output")).toContainText("Inspect");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = aside.getByRole("textbox", { name: "Reply to BTW" });
  await reply.fill("draft belongs to this thread");

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
  await progress.getByText("show output", { exact: true }).click();
  await expect(progress.locator(".agent-tool-output")).toContainText("Inspect");

  await reply.fill("agent-terminal");
  await reply.press("Enter");
  await expect(aside).toContainText("agent terminal finished");
  const commandActivity = aside.locator(".agent-entry-activity").last();
  await commandActivity.locator("summary").click();
  const command = commandActivity.locator(".agent-activity-step", { hasText: "echo hello" });
  await command.getByText("show output", { exact: true }).click();
  await expect(command.locator(".agent-tool-output")).toContainText("hello");
  await expect(progress.locator(".agent-tool-output")).toContainText("Inspect");
});
