import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

test("BTW completes and executes agent commands in forks without changing the primary", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "btw-commands");
  const composer = surface.locator("[data-agent-composer] textarea");
  const picker = surface.getByRole("listbox", { name: "Slash commands" });
  await submitAcpDraft(surface, "main anchor");
  await expect(surface).toContainText("echo: main anchor");

  await composer.fill("/btw /");
  await expect(picker).toContainText("/report-weavie-bug");
  await expect(picker).toContainText("/review");
  await expect(
    picker.locator(".agent-slash-name").filter({ hasText: /^\/(clear|btw)$/ }),
  ).toHaveCount(0);
  await composer.fill("/btw   /request-weavie");
  await composer.press("Tab");
  await expect(composer).toHaveValue("/btw   /request-weavie-feature");
  await composer.press("End");
  await composer.press("Space");
  await composer.pressSequentially("invented details");
  await submitAcpDraft(surface, await composer.inputValue());
  await expect(surface.locator(".agent-aside").first()).toContainText(
    "Received Weavie MCP prompt: request-weavie-feature",
  );

  await composer.fill("/btw /compact");
  await composer.press("Enter");
  await expect(surface.locator(".agent-aside")).toHaveCount(2);
  await expect(surface.locator(".agent-aside").last()).toContainText("Compacting completed.");
  await submitAcpDraft(surface, "/btw /clear");
  await expect(surface).toContainText("/clear cannot run in a side conversation.");
  await expect(surface).toContainText("echo: main anchor");
  await expect(surface.locator(".agent-aside")).toHaveCount(2);
  await expect(composer).toHaveValue("/btw /clear");

  const records = (
    await readFile(join(weavie.home, ".weavie", "fake-acp-state", "wire-prompts.jsonl"), "utf8")
  )
    .trim()
    .split(/\r?\n/)
    .map((line) => JSON.parse(line));
  expect(records).toHaveLength(3);
  expect(records[1].parameters.sessionId).not.toBe(records[0].parameters.sessionId);
  expect(records[2].parameters.sessionId).not.toBe(records[0].parameters.sessionId);
  expect(records[1].parameters.prompt[0].text).toContain(
    "Never include source code from non-public repositories",
  );
  expect(records[1].parameters.prompt[1].text).toBe("invented details");
  expect(records[2].parameters.prompt).toEqual([{ type: "text", text: "/compact" }]);
});
