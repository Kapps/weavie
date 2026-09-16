import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { navChord } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

// SCENE (video tour): the parked toolbar now shows "file i/N" immediately — synchronously, from the known
// file set — instead of waiting on the first file's Monaco diff paint. Modeled on
// functional/diff-review.spec.ts's "multi-file review walk" describe block, with holds so the recording
// SHOWS the state. Not a committed regression spec (that already exists); this exists only to record a .webm.
const FILE_COUNT = 20;
const steps = Array.from({ length: FILE_COUNT }, (_, i) =>
  appliedEdit(`bulk-${i}.txt`, `change ${i}\nsecond line\n`),
).flat();

test.use({ fakeScript: { steps } });

const hold = (page: import("@playwright/test").Page, ms: number) => page.waitForTimeout(ms);

test("parked toolbar reads file 1/20 the instant the review opens, before any diff paints", async ({
  page,
}) => {
  // Open an UNCHANGED file: the 20-file review parks over it without touching the editor.
  await openFile(page, "README.md");

  const sub = page.locator(".weavie-inline-stack-sub");
  await expect(sub).toContainText(`file 1/${FILE_COUNT}`, { timeout: 5_000 });
  await expect(page.locator(".weavie-inline-file")).toHaveCount(2); // ← / → file-step buttons
  await hold(page, 2500); // let the camera sit on "file 1/20 · press ↓ to start"

  // Step in — the live toolbar takes over, now showing the same "file i/N" position plus the active
  // per-file change counter ("file 1/20 · change 1/1").
  await page.keyboard.press(navChord("ArrowDown"));
  await expect(sub).toContainText(`file 1/${FILE_COUNT} · change`, { timeout: 5_000 });
  await hold(page, 2000);
});
