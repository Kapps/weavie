import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("every reply of a turn shows above its one row of work", async ({
  page,
  weavie,
}, testInfo) => {
  const surface = await createAcpSession(page, "activity-grouping");
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await submitAcpDraft(surface, "replies-claude");
  const replies = surface.locator(".agent-entry-message.agent-tone-assistant");
  const work = surface.locator(".agent-entry-activity");
  const rows = () =>
    surface
      .locator(".agent-entry-message.agent-tone-assistant, .agent-entry-activity")
      .evaluateAll((entries) =>
        entries.map((entry) => (entry.matches(".agent-entry-activity") ? "work" : "reply")),
      );
  await expect(replies).toHaveCount(6);
  await expect(surface.getByRole("button", { name: /Earlier updates/ })).toHaveCount(0);
  await expect(work).toHaveCount(1);
  await expect(work.locator("summary")).toHaveText("searched 4 times");
  // The row follows the note its latest step came after, ahead of the reply the turn ended on.
  expect(await rows()).toEqual(["reply", "reply", "reply", "reply", "reply", "work", "reply"]);
  await surface.screenshot({ path: testInfo.outputPath("turn.png") });

  // Work after the turn ended is a row of its own.
  await writeFile(join(workspace, "release-replies-late"), "");
  await expect(replies).toHaveCount(7);
  await expect(work.locator("summary")).toHaveText(["searched 4 times", "searched 1 time"]);
  await page.reload();
  await expect(replies).toHaveCount(7);
  await expect(work.locator("summary")).toHaveText(["searched 4 times", "searched 1 time"]);
});
