import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { gateFor, inboundGate } from "../harness/inbound-gate";
import { appliedEdit } from "../harness/review";

// A hunk's ranges are only meaningful in the review baseline they were diffed against. Keeping an earlier hunk that
// inserts lines moves that baseline, so a revert sent before the refreshed diff reaches the page must be refused,
// never spliced at the old offsets.
const baseline = `${Array.from({ length: 12 }, (_, i) => `line ${i + 1} base`).join("\n")}\n`;
const changed = baseline
  .replace("line 2 base\n", "line 2 agent\ninserted A\ninserted B\n")
  .replace("line 6 base", "line 6 agent");

test.use({
  workspaceSeed: { run: (workspace) => writeFile(join(workspace, "story.txt"), baseline) },
  fakeScript: { steps: appliedEdit("story.txt", changed) },
  preNavigate: inboundGate,
});

test("a revert diffed against a baseline a keep has moved is refused, then lands after the refresh", async ({
  page,
  weavie,
}) => {
  const contents = () => readFile(join(weavie.workspace, "story.txt"), "utf8");
  const keeps = page.locator(".weavie-inline-pending-keep");
  const reverts = page.locator(".weavie-inline-pending-revert");
  await openFile(page, "story.txt");
  await expect(keeps).toHaveCount(2);

  // The page still holds the pre-keep diff when it sends the revert.
  const gate = gateFor(page);
  gate.hold();
  await keeps.first().click();
  await reverts.nth(1).click();
  gate.release();

  // The warning is the host's ruling on the revert, so by then the disk shows what it did.
  await expect(page.locator(".toast.toast-warn", { hasText: /story\.txt/ })).toBeVisible();
  expect(await contents()).toBe(changed);

  await expect(reverts).toHaveCount(1);
  await reverts.click();
  await expect.poll(contents).toBe(changed.replace("line 6 agent", "line 6 base"));
});
