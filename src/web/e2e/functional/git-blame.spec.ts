import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The blame annotation must be PAINTED, not merely decorated. Every layer beneath the editor already has
// coverage — BlamePorcelainTests, UnifiedDiffTests, GitBlameIntegrationTests, HostCoreGitBlameTests,
// blame-model.test.ts — and all of them passed while the user saw nothing: the annotation is injected text on
// a zero-width range, which Monaco discards unless the decoration carries `showIfCollapsed`. Only asserting
// the rendered element catches that class of defect, so these assertions are deliberately about the DOM.
//
// The harness workspace is a git repo whose seed files are committed as "seed" by "Weavie E2E".

test("every line carries a blame annotation naming who last changed it", async ({ page }) => {
  await openFile(page, "hello.ts");

  const annotations = page.locator(".weavie-blame");
  await expect(annotations.first()).toBeVisible();
  // `\s` rather than literal spaces: Monaco renders the injected text's spacing as non-breaking spaces, and
  // the label is padded away from the code it follows, so neither the gap nor the separators are plain " ".
  await expect(annotations.first()).toContainText(/Weavie\sE2E,\s.+\s•\sseed/);
  // Annotated per visible line, not just once for the file.
  expect(await annotations.count()).toBeGreaterThan(1);
});

test("clicking an annotation opens the change that produced the line", async ({ page }) => {
  await openFile(page, "hello.ts");
  // The annotation is the feature's front door — the entry point that needs no palette.
  await page.locator(".weavie-blame").first().click();

  const popover = page.getByRole("dialog", { name: "Git blame" });
  await expect(popover).toBeVisible();
  await expect(popover.locator(".weavie-blame-subject")).toHaveText("seed");
  // The body is the hunk, with the blamed line marked inside it.
  await expect(popover.locator(".weavie-blame-hunk-line").first()).toContainText("@@");
  await expect(popover.locator(".weavie-blame-focus")).toHaveCount(1);
  // The file seeded in one commit, so its own history is that commit.
  await expect(popover.locator(".weavie-blame-entry")).toHaveCount(1);

  await page.keyboard.press("Escape");
  await expect(popover).toHaveCount(0);
});

test("turning blame off removes the annotations and turning it back on restores them", async ({
  page,
}) => {
  await openFile(page, "hello.ts");
  await expect(page.locator(".weavie-blame").first()).toBeVisible();

  await runCommand(page, "Toggle Git Blame Annotations");
  await expect(page.locator(".weavie-blame")).toHaveCount(0);

  await runCommand(page, "Toggle Git Blame Annotations");
  await expect(page.locator(".weavie-blame").first()).toBeVisible();
});

test("a line is annotated exactly once when a collapsed comment splits the viewport", async ({
  page,
  weavie,
}) => {
  // Weavie collapses doc comments into a view zone (comment prose), which splits the editor into several
  // visible ranges. Widening each by the overscan makes neighbours overlap, so without a dedupe the same line
  // is decorated — and painted — two or three times. `hello.ts` has no doc comment and cannot catch it.
  // The doc comment sits in the MIDDLE: collapsing it leaves a visible range on either side. At the top it
  // would only shorten the single range, and the overlap this guards against never happens.
  const name = "documented.ts";
  await writeFile(
    join(weavie.workspace, name),
    "export const first = 1;\nexport const second = 2;\n" +
      "/**\n * A documented function, so comment prose collapses this block.\n */\n" +
      "export function documented(): number {\n  return first + second;\n}\n\n" +
      "export const answer = documented();\n",
  );
  execFileSync("git", ["add", "-A"], { cwd: weavie.workspace, stdio: "ignore" });
  execFileSync(
    "git",
    [
      "-c",
      "user.email=e2e@example.com",
      "-c",
      "user.name=Weavie E2E",
      "-c",
      "commit.gpgsign=false",
      "commit",
      "-q",
      "-m",
      "document it",
    ],
    { cwd: weavie.workspace, stdio: "ignore" },
  );

  await openFile(page, name);
  await expect(page.locator(".weavie-blame").first()).toBeVisible();

  // Count the labels against the lines they sit on: one apiece, however the viewport is carved up.
  const perLine = await page.evaluate(() =>
    [...document.querySelectorAll(".view-line")].map(
      (line) => line.querySelectorAll(".weavie-blame").length,
    ),
  );
  expect(perLine.filter((count) => count > 0).length).toBeGreaterThan(0);
  expect(perLine.every((count) => count <= 1)).toBe(true);
});

test("Show Blame answers for the cursor's line even with annotations off", async ({ page }) => {
  await openFile(page, "hello.ts");
  await runCommand(page, "Toggle Git Blame Annotations");
  await expect(page.locator(".weavie-blame")).toHaveCount(0);

  // Asking for a line's blame is a question about the file, not about whether annotations are painted.
  await runCommand(page, "Show Blame for This Line");

  const popover = page.getByRole("dialog", { name: "Git blame" });
  await expect(popover).toBeVisible();
  await expect(popover.locator(".weavie-blame-subject")).toHaveText("seed");
});
