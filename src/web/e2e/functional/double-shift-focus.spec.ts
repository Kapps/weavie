import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { focusEditor } from "../harness/navigator";
import type { WeavieWindow } from "../harness/weavie-window";

// The "double-shift" gesture (tap Shift twice quickly, IntelliJ-style — see double-shift.ts) opens the
// file-search omnibar from ANY window/document Shift key event, so it must never fire off a Shift held as
// a mouse-click modifier. Two shift-clicks extending a selection in quick succession — a completely
// ordinary way to reach into a comment block, or just extend a selection further — put a Shift keyup
// within the gesture's tap window of a PRIOR Shift keyup, exactly like a real double-tap would. Root-caused
// 2026-08-23 from comment-prose-selection.spec.ts's "leaves it raw" flake losing editor focus after a
// shift-click (https://github.com/Kapps/weavie/actions/runs/32616592610/job/97139307788); see
// double-shift.ts's onMouseDown for the fix.
test("two quick shift-clicks extending a selection don't steal focus to the omnibar", async ({
  weavie,
  page,
}) => {
  writeFileSync(
    join(weavie.workspace, "lines.ts"),
    ["const a = 1;", "const b = 2;", "const c = 3;", "const d = 4;", "const e = 5;", ""].join("\n"),
  );
  await openFile(page, "lines.ts");
  await focusEditor(page);

  await page.locator(".view-line", { hasText: "const a" }).click();

  // Two shift-clicks back to back, each holding Shift only around its own click — the ordinary way a user
  // extends a selection further with a second shift-click, not a deliberate Shift/Shift tap.
  await page.keyboard.down("Shift");
  await page.locator(".view-line", { hasText: "const c" }).click();
  await page.keyboard.up("Shift");
  await page.keyboard.down("Shift");
  await page.locator(".view-line", { hasText: "const e" }).click();
  await page.keyboard.up("Shift");

  // The selection extended normally...
  const endLine = await page.evaluate(
    () => (window as WeavieWindow).__WEAVIE_EDITOR__?.getSelection()?.endLineNumber ?? null,
  );
  expect(endLine).toBe(5);

  // ...and the editor kept focus — the omnibar never opened and stole it.
  await expect
    .poll(() =>
      page.evaluate(() => (window as WeavieWindow).__WEAVIE_EDITOR__?.hasTextFocus() ?? false),
    )
    .toBe(true);
  await expect(page.locator(".tb-omnibar-box.open")).toHaveCount(0);
});
