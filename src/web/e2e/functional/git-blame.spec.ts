import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openFile, pressDocumentEnd, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import type { EditorHandle, ModelHandle, WeavieWindow } from "../harness/weavie-window";

// Writes and commits one file in the harness workspace, so a test can build the blame shape it needs.
async function commitFile(
  workspace: string,
  name: string,
  content: string,
  message: string,
): Promise<void> {
  await writeFile(join(workspace, name), content);
  execFileSync("git", ["add", "-A"], { cwd: workspace, stdio: "ignore" });
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
      message,
    ],
    { cwd: workspace, stdio: "ignore" },
  );
}

// Every rendered line, in the editor's own order: its text with the label's stripped out — so a line reads as
// the code the user sees rather than as code plus annotation — how many annotations sit on it, and where it is.
// Runs in the page; hand it to `page.evaluate`.
function annotatedLines(): { text: string; labels: number; left: number; middle: number }[] {
  return (
    [...document.querySelectorAll(".view-line")]
      .map((line) => {
        const copy = line.cloneNode(true) as HTMLElement;
        for (const label of copy.querySelectorAll(".weavie-blame")) {
          label.remove();
        }
        const box = line.getBoundingClientRect();
        return {
          // Monaco renders a line's spacing as non-breaking spaces: a blank line is only blank once they are.
          text: (copy.textContent ?? "").replace(/\u00a0/g, " ").trim(),
          labels: line.querySelectorAll(".weavie-blame").length,
          left: box.left,
          middle: box.top + box.height / 2,
        };
      })
      // Monaco positions line elements absolutely and reuses them, so their DOM order is not the file's.
      .sort((first, second) => first.middle - second.middle)
  );
}

// The blame annotation must be PAINTED, not merely decorated. Every layer beneath the editor already has
// coverage — BlamePorcelainTests, UnifiedDiffTests, GitBlameIntegrationTests, HostCoreGitBlameTests,
// blame-model.test.ts — and all of them passed while the user saw nothing: the annotation is injected text on
// a zero-width range, which Monaco discards unless the decoration carries `showIfCollapsed`. Only asserting
// the rendered element catches that class of defect, so these assertions are deliberately about the DOM.
//
// The harness workspace is a git repo whose seed files are committed as "seed" by "Weavie E2E".

test("the cursor's line is annotated by default, and only it", async ({ page }) => {
  await openFile(page, "hello.ts");

  const annotations = page.locator(".weavie-blame");
  await expect(annotations.first()).toBeVisible();
  // `\s` rather than literal spaces: Monaco renders the injected text's spacing as non-breaking spaces, so
  // the label's own separators are not plain " ".
  await expect(annotations.first()).toContainText(/Weavie\sE2E,\s.+\s•\sseed/);
  // `currentLine` is the default: one label, on the line the cursor is on — not down the whole file.
  await expect(annotations).toHaveCount(1);
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

// `all` is not the default, so these drive the setting the way a user would — over the capability registry.
test.describe("annotating every run", () => {
  test.use({
    fakeScript: {
      steps: [{ op: "mcp", tool: "setSetting", args: { key: "editor.gitBlame", value: "all" } }],
    },
  });

  test("one commit's stretch of lines is labelled once, at its top", async ({ page }) => {
    await openFile(page, "hello.ts");

    // Every line of hello.ts came from the same commit, so `all` must label exactly one — the run's first
    // line. Repeating the same label down every line is what made this mode unreadable.
    const annotations = page.locator(".weavie-blame");
    await expect(annotations).toHaveCount(1, { timeout: 15_000 });
    await expect(annotations.first()).toContainText(/Weavie\sE2E,\s.+\s•\sseed/);

    const lines = await page.evaluate(annotatedLines);
    expect(lines.findIndex((line) => line.labels > 0)).toBe(0);
  });

  test("a line is annotated exactly once when a collapsed comment splits the viewport", async ({
    page,
    weavie,
  }) => {
    // Weavie collapses doc comments into a view zone (comment prose), which splits the editor into several
    // visible ranges. Widening each by the overscan makes neighbours overlap, so without a dedupe the same
    // line is decorated — and painted — two or three times. `hello.ts` has no doc comment and cannot catch it.
    // The doc comment sits in the MIDDLE: collapsing it leaves a visible range on either side. At the top it
    // would only shorten the single range, and the overlap this guards against never happens.
    const name = "documented.ts";
    await commitFile(
      weavie.workspace,
      name,
      "export const first = 1;\nexport const second = 2;\n" +
        "/**\n * A documented function, so comment prose collapses this block.\n */\n" +
        "export function documented(): number {\n  return first + second;\n}\n\n" +
        "export const answer = documented();\n",
      "document it",
    );

    await openFile(page, name);
    await expect(page.locator(".weavie-blame").first()).toBeVisible({ timeout: 15_000 });

    // Count the labels against the lines they sit on: one apiece, however the viewport is carved up.
    const perLine = (await page.evaluate(annotatedLines)).map((line) => line.labels);
    expect(perLine.filter((count) => count > 0).length).toBeGreaterThan(0);
    expect(perLine.every((count) => count <= 1)).toBe(true);
  });

  test("a run opening on a blank line is labelled on its code, not on the blank", async ({
    page,
    weavie,
  }) => {
    // A commit that appends a block owns the blank line separating it from what was there before, so its run
    // starts on an empty line. An annotation there sits where the line's own text would be and describes
    // nothing the user can see.
    const name = "appended.ts";
    await commitFile(weavie.workspace, name, "export const first = 1;\n", "add first");
    await commitFile(
      weavie.workspace,
      name,
      "export const first = 1;\n\nexport const added = 2;\n",
      "append a block",
    );

    await openFile(page, name);
    await expect(page.locator(".weavie-blame")).toHaveCount(2, { timeout: 15_000 });

    const lines = await page.evaluate(annotatedLines);
    expect(lines.filter((line) => line.text === "").every((line) => line.labels === 0)).toBe(true);
    expect(lines.find((line) => line.text.includes("added"))?.labels).toBe(1);
  });
});

test("the cursor on a blank line is left unannotated", async ({ page }) => {
  await openFile(page, "hello.ts");
  await expect(page.locator(".weavie-blame")).toHaveCount(1);

  await pressDocumentEnd(page);
  expect(
    await page.evaluate(() => {
      const editor = (window as WeavieWindow).__WEAVIE_EDITOR__ as EditorHandle;
      return editor.getModel()?.getLineContent(editor.getPosition()!.lineNumber);
    }),
  ).toBe("");

  await expect(page.locator(".weavie-blame")).toHaveCount(0);
});

// Where the annotated line's own code ends and where its annotation begins — the gap between them. Runs in
// the page; hand it to `page.evaluate`.
function annotationGeometry(): { code: number; annotation: number; middle: number } {
  const label = document.querySelector(".weavie-blame") as HTMLElement;
  const line = label.closest(".view-line") as HTMLElement;
  const box = label.getBoundingClientRect();
  return {
    // Every span of the line that is not the annotation or the gap in front of it, so `code` is where the
    // code itself ends however that gap is drawn.
    code: Math.max(
      ...[...line.querySelectorAll("span > span")]
        .filter((span) => !span.className.includes("weavie-blame"))
        .map((span) => span.getBoundingClientRect().right),
    ),
    annotation: box.left,
    middle: box.top + box.height / 2,
  };
}

// The line geometry, retried as a whole: Monaco replaces a line's spans as it re-renders, and a detached one
// measures as zero.
async function annotatedLineGeometry(page: Page): Promise<ReturnType<typeof annotationGeometry>> {
  let geometry: ReturnType<typeof annotationGeometry> | undefined;
  await expect(async () => {
    geometry = await page.evaluate(annotationGeometry);
    // A line whose spans were all replaced mid-measure reports no code at all, which must retry, not pass.
    expect(geometry.code).toBeGreaterThan(0);
    expect(geometry.annotation - geometry.code).toBeGreaterThan(20);
  }).toPass();
  return geometry as ReturnType<typeof annotationGeometry>;
}

test("the end of an annotated line measures at its code, not past the gap", async ({ page }) => {
  await openFile(page, "hello.ts");
  await expect(page.locator(".weavie-blame").first()).toBeVisible();

  // Select the line's last character, leaving the caret at the line's end — where the gap begins. Monaco
  // measures that column at the FIRST character injected there, so drawing the gap as space in front of the
  // annotation (a margin, a padding) put the caret and the selection band a whole gap to the right, beside the
  // annotation instead of at the code. The gap is injected text of its own for exactly that reason.
  await page.keyboard.press("End");
  await page.keyboard.press("ArrowLeft");
  await page.keyboard.press("Shift+ArrowRight");
  await expect(page.locator(".view-overlays .selected-text")).toHaveCount(1);

  const geometry = await annotatedLineGeometry(page);
  const painted = await page.evaluate(() => ({
    // Monaco insets the caret by a pixel so a caret at column 1 stays on screen.
    caret: (document.querySelector(".cursors-layer .cursor") as HTMLElement).getBoundingClientRect()
      .left,
    selection: (
      document.querySelector(".view-overlays .selected-text") as HTMLElement
    ).getBoundingClientRect().right,
  }));

  expect(Math.abs(painted.caret - geometry.code)).toBeLessThan(2);
  expect(Math.abs(painted.selection - geometry.code)).toBeLessThan(2);
});

test("the gap after a line belongs to the line, not to its annotation", async ({ page }) => {
  await openFile(page, "hello.ts");
  await expect(page.locator(".weavie-blame").first()).toBeVisible();

  // Clicking in the gap is how a user puts the caret at the end of a line; it must not open the popover.
  const geometry = await annotatedLineGeometry(page);
  await page.mouse.click((geometry.code + geometry.annotation) / 2, geometry.middle);
  await expect(page.getByRole("dialog", { name: "Git blame" })).toHaveCount(0);

  // And it puts the caret at the end of the line, which is what the click was for.
  const caret = await page.evaluate(() => {
    const editor = (window as unknown as WeavieWindow).__WEAVIE_EDITOR__ as EditorHandle;
    const position = editor.getPosition() as { lineNumber: number; column: number };
    const model = editor.getModel() as ModelHandle;
    return { column: position.column, end: model.getLineContent(position.lineNumber).length + 1 };
  });
  expect(caret.column).toBe(caret.end);

  // The label itself is still the front door.
  await page.locator(".weavie-blame").first().click();
  await expect(page.getByRole("dialog", { name: "Git blame" })).toBeVisible();
});

test("a long change leaves the popover's history on screen", async ({ page }) => {
  // long.ts is 160 lines from a single commit, so its hunk is the whole file — the shape that used to squeeze
  // the history below it into a sliver and push its buttons past the panel's edge.
  await openFile(page, "long.ts");
  await page.locator(".weavie-blame").first().click();

  const popover = page.getByRole("dialog", { name: "Git blame" });
  await expect(popover).toBeVisible();
  await expect(popover.locator(".weavie-blame-hunk-line").nth(100)).toBeAttached();
  await expect(popover.locator(".weavie-blame-entry")).toHaveCount(1);

  const geometry = await page.evaluate(() => {
    const bounds = (selector: string) => {
      const box = document.querySelector(selector)?.getBoundingClientRect();
      return { top: box?.top ?? 0, bottom: box?.bottom ?? 0, height: box?.height ?? 0 };
    };
    return {
      viewport: window.innerHeight,
      panel: bounds(".weavie-blame-popover"),
      hunk: bounds(".weavie-blame-hunk"),
      tabs: bounds(".weavie-blame-tabs"),
      entry: bounds(".weavie-blame-entry"),
    };
  });

  // The change takes a bounded share of the panel...
  expect(geometry.hunk.height).toBeLessThan(geometry.viewport * 0.45);
  // ...so the history's tabs and entries sit inside the panel rather than clipped off its bottom edge.
  expect(geometry.tabs.bottom).toBeLessThanOrEqual(geometry.panel.bottom);
  expect(geometry.entry.bottom).toBeLessThanOrEqual(geometry.panel.bottom);
  expect(geometry.entry.height).toBeGreaterThan(10);
  expect(geometry.panel.bottom).toBeLessThanOrEqual(geometry.viewport);
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
