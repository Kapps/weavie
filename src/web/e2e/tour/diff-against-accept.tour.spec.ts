import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// SCENE 1 (video tour): Diff Against HEAD now carries full Keep (accept) / Revert (reject) — it used to be
// read-only. Modeled on functional/diff-against.spec.ts, with holds so the recording SHOWS each state. Not a
// committed regression spec (that already exists); this exists only to record a .webm.
const hold = (page: import("@playwright/test").Page, ms: number) => page.waitForTimeout(ms);

test("Diff Against HEAD: Keep + Revert present, Revert backs the edit out", async ({
  page,
  weavie,
}) => {
  // An uncommitted edit to a seeded file — exactly what "Diff Against HEAD" reviews.
  const notes = join(weavie.workspace, "notes.txt");
  await writeFile(notes, "just plain text\nplus an uncommitted line\n");

  await runCommand(page, "Diff Against HEAD");

  const toolbar = page.locator(".weavie-inline-toolbar");
  await expect(toolbar).toBeVisible({ timeout: 20_000 });
  await expect(page.locator(".weavie-inline-stack-name")).toHaveText("notes.txt");
  await expect(page.locator(".weavie-inline-stack-sub")).toContainText("vs HEAD");
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();

  // The point of the change: both action buttons are here (Comment absent — a local ref has no forge).
  await expect(toolbar.locator(".weavie-inline-accept")).toBeVisible();
  await expect(toolbar.locator(".weavie-inline-reject")).toBeVisible();
  await expect(toolbar.locator(".weavie-inline-comment")).toHaveCount(0);
  await toolbar.locator(".weavie-inline-accept").hover(); // surface the "Keep" tooltip for the camera
  await hold(page, 2200);
  await toolbar.locator(".weavie-inline-reject").hover(); // surface the "Revert" tooltip
  await hold(page, 1500);

  // Revert → the uncommitted line is backed out on disk and its added marker clears, exactly as a turn revert.
  await toolbar.locator(".weavie-inline-reject").click();
  await expect(page.locator(".weavie-inline-added")).toHaveCount(0, { timeout: 10_000 });
  await hold(page, 2000);
});
