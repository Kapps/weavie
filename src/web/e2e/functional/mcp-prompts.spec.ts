import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

test.use({ video: { mode: "on", size: { width: 1280, height: 800 } }, launchOptions: { slowMo: 160 } });

test("native slash prompts are available before the first turn and expand only for the agent", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "mcp-prompts");
  const composer = surface.locator("[data-agent-composer] textarea");
  const picker = surface.getByRole("listbox", { name: "Slash commands" });
  const userMessages = surface.locator(".agent-entry-message.agent-tone-user .agent-entry-text");
  const assistantMessages = surface.locator(".agent-entry-message.agent-tone-assistant");

  await expect(surface.locator(".agent-entry")).toHaveCount(0);
  await composer.fill("/");
  await expect(picker).toContainText("/report-weavie-bug");
  await expect(picker).toContainText("/request-weavie-feature");
  await page.waitForTimeout(1400);
  await composer.fill("/report-weavie-bug");
  await expect(picker.getByRole("option")).toHaveCount(1);
  await composer.press("Enter");
  await expect(assistantMessages.last()).toContainText(
    "Received Weavie MCP prompt: report-weavie-bug",
  );
  await expect(userMessages.last()).toHaveText("/report-weavie-bug");
  await expect(composer).toHaveValue("");
  await page.waitForTimeout(1400);

  const request = "/request-weavie-feature Make navigation easier";
  await submitAcpDraft(surface, request);
  await expect(assistantMessages.last()).toContainText(
    "Received Weavie MCP prompt: request-weavie-feature",
  );
  await expect(userMessages.last()).toHaveText(request);
  await page.waitForTimeout(1400);

  await composer.fill("/setup-workspace");
  await expect(picker.getByRole("option")).toHaveCount(1);
  await composer.press("Enter");
  await expect(assistantMessages.last()).toContainText(
    "Received Weavie MCP prompt: setup-workspace",
  );
  await expect(userMessages.last()).toHaveText("/setup-workspace");
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
  await expect(userMessages).toHaveCount(3);
  await page.waitForTimeout(1400);

  const records = (
    await readFile(join(weavie.home, ".weavie", "fake-acp-state", "wire-prompts.jsonl"), "utf8")
  )
    .trim()
    .split(/\r?\n/)
    .map((line) => JSON.parse(line));
  expect(records).toHaveLength(3);
  for (const record of records) {
    expect(record.method).toBe("session/prompt");
    expect(record.parameters.prompt[0].type).toBe("text");
    expect(record.parameters.prompt[0].text).not.toMatch(/^\//);
  }
  for (const record of records.slice(0, 2)) {
    const text = record.parameters.prompt[0].text;
    expect(text).toContain("Never include source code from non-public repositories");
    expect(text).toContain("Treat unknown repository visibility as non-public");
    expect(text).toContain("Kapps/weavie");
    expect(text).toContain("obtain approval of that concrete content before publishing");
  }
  expect(records[0].parameters.prompt[0].text).toContain("bug report");
  expect(records[1].parameters.prompt[0].text).toContain("feature request");
  expect(records[1].parameters.prompt[1]).toEqual({ type: "text", text: "Make navigation easier" });
  expect(records[2].parameters.prompt[0].text).toContain("worktree.setupCommand");
  expect(records[2].parameters.prompt[0].text).toContain("test.profile");
});
