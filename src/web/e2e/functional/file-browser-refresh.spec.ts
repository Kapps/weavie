import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The file browser must reflect files created outside the page — by the agent, a terminal command (e.g. a
// branch switch), or any external tool — without a reload: the host's workspace watcher re-lists a changed
// directory the browser has open and pushes the fresh listing. Full stack: real FileSystemWatcher → session
// refresh → dir-listing push → tree row.
test("a file created on disk appears in the open file browser without a reload", async ({
  page,
  weavie,
}) => {
  await runCommand(page, "Toggle File Browser");
  await expect(page.locator(".browser-panel")).toBeVisible();
  await expect(page.locator(".browser-row", { hasText: "hello.ts" })).toBeVisible();

  // Created behind the page's back, exactly like `touch` in the shell pane.
  writeFileSync(join(weavie.workspace, "fresh-from-terminal.txt"), "hi\n");

  // The watcher debounces (250ms) then the host re-lists and re-pushes — no toggle, no reload.
  await expect(page.locator(".browser-row", { hasText: "fresh-from-terminal.txt" })).toBeVisible({
    timeout: 10_000,
  });
});
