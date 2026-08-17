import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

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

  // Click column 1 of the file's blank line, so the caret lands on it the way a user's would.
  const blank = (await page.evaluate(annotatedLines)).find((line) => line.text === "");
  expect(blank).toBeDefined();
  await page.mouse.click((blank?.left ?? 0) + 4, blank?.middle ?? 0);

  await expect(page.locator(".weavie-blame")).toHaveCount(0);
});

test("the gap after a line belongs to the line, not to its annotation", async ({ page }) => {
  await openFile(page, "hello.ts");
  await expect(page.locator(".weavie-blame").first()).toBeVisible();

  // The annotation is held clear of the code by a margin, which lies outside its hit area. Read in one go
  // from a freshly queried element, and retried: Monaco replaces a line's spans as it re-renders, and a
  // detached one answers every computed style with "".
  let marker: { gap: number; left: number; middle: number } | undefined;
  await expect(async () => {
    marker = await page.evaluate(() => {
      const label = document.querySelector(".weavie-blame");
      const box = label?.getBoundingClientRect();
      return label === null || box === undefined
        ? undefined
        : {
            gap: Number.parseFloat(getComputedStyle(label).marginLeft),
            left: box.left,
            middle: box.top + box.height / 2,
          };
    });
    expect(marker?.gap ?? Number.NaN).toBeGreaterThan(20);
  }).toPass();

  // Clicking in that gap is how a user puts the caret at the end of a line; it must not open the popover.
  await page.mouse.click((marker?.left ?? 0) - (marker?.gap ?? 0) / 2, marker?.middle ?? 0);
  await expect(page.getByRole("dialog", { name: "Git blame" })).toHaveCount(0);

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
