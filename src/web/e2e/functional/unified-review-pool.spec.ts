import { readFile, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import { scrollReview } from "../harness/review-scroll";

test.use({
  fakeScript: {
    steps: ["a", "b", "c"].flatMap((name) =>
      appliedEdit(
        `${name}.txt`,
        Array.from({ length: 500 }, (_, index) => `${name} line ${index}`).join("\n"),
      ),
    ),
  },
});

test.describe("lease editability", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "waitFile", path: "{{WORKSPACE}}/.git/pool-settings" },
        { op: "mcp", tool: "setSetting", args: { key: "editor.wordWrap", value: "on" } },
      ],
    },
  });

  test("a deleted snapshot can return its shell to an editable file across live settings changes", async ({
    page,
    weavie,
  }) => {
    await unlink(join(weavie.workspace, "notes.txt"));
    const path = join(weavie.workspace, "z.txt");
    await writeFile(path, "editable\n");
    await runCommand(page, "Diff Against HEAD");
    await awaitReviewSet(page, ["notes.txt", "z.txt"]);
    await runCommand(page, "Review Changes");
    const section = (name: string) =>
      page.locator(".unified-review-file", {
        has: page.locator(".unified-review-file-name", { hasText: name }),
      });
    const editable = section("z.txt");
    const deleted = section("notes.txt");
    await expect(editable.locator(".monaco-editor")).toBeVisible();
    await editable.locator(".unified-review-file-toggle").click();
    await expect(editable.locator(".monaco-editor")).toHaveCount(0);
    await deleted.locator(".unified-review-file-toggle").click();
    await expect(deleted.locator(".monaco-editor")).toBeVisible();
    const snapshot = await page.evaluate(() => {
      const monaco = window.__WEAVIE_MONACO__!;
      const editor = monaco.editor
        .getEditors()
        .find((item) => item.getDomNode()?.closest(".unified-review-file"))!;
      return {
        id: editor.getId(),
        readOnly: editor.getOption(monaco.editor.EditorOption.readOnly),
      };
    });
    expect(snapshot.readOnly).toBe(true);
    await deleted.locator(".unified-review-file-toggle").click();
    await editable.locator(".unified-review-file-toggle").click();
    await expect(editable.locator(".monaco-editor")).toBeVisible();
    await runCommand(page, "Increase Font Size");
    await writeFile(join(weavie.workspace, ".git", "pool-settings"), "ready");
    await expect
      .poll(() =>
        page.evaluate(() => {
          const monaco = window.__WEAVIE_MONACO__!;
          const editor = monaco.editor
            .getEditors()
            .find((item) => item.getModel()?.uri.path.endsWith("/z.txt"))!;
          return {
            id: editor.getId(),
            readOnly: editor.getOption(monaco.editor.EditorOption.readOnly),
            fontSize: editor.getOption(monaco.editor.EditorOption.fontSize),
            wordWrap: editor.getOption(monaco.editor.EditorOption.wordWrap),
          };
        }),
      )
      .toEqual({ id: snapshot.id, readOnly: false, fontSize: 17, wordWrap: "on" });
    await page.evaluate(() => {
      const editor = window
        .__WEAVIE_MONACO__!.editor.getEditors()
        .find((item) => item.getModel()?.uri.path.endsWith("/z.txt"))!;
      editor.setPosition({ lineNumber: 1, column: 1 });
      editor.focus();
    });
    await page.keyboard.type("pooled ");
    await page.keyboard.press("ControlOrMeta+s");
    await expect.poll(() => readFile(path, "utf8")).toBe("pooled editable\n");
  });
});

test("review leases reuse editors without retaining find or selection state and close with the surface", async ({
  page,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("3");
  const existing = await page.evaluate(
    () => window.__WEAVIE_MONACO__?.editor.getEditors().map((item) => item.getId()) ?? [],
  );
  await page.locator(".editor-empty-review").click();
  const first = page.locator('.unified-review-file[data-index="1"] .monaco-editor');
  const last = page.locator('.unified-review-file[data-index="3"] .monaco-editor');
  await expect(first).toBeVisible();
  await expect(page.locator(".unified-review-notice")).toHaveCount(0);
  const initial = await page.evaluate(async () => {
    const editors = window.__WEAVIE_MONACO__!.editor.getEditors();
    const editor = editors.find((item) => item.getModel()?.uri.path.endsWith("/a.txt"))!;
    editor.setPosition({ lineNumber: 45, column: 3 });
    await editor.getAction("actions.find")!.run();
    return editors.map((item) => item.getId());
  });
  await expect(first.locator(".find-widget.visible")).toBeVisible();
  await scrollReview(page, "end");
  await expect(last).toBeVisible();
  await expect(first).toHaveCount(0);
  await expect(last.locator(".find-widget.visible")).toHaveCount(0);
  const atEnd = await page.evaluate(() => {
    const editors = window.__WEAVIE_MONACO__!.editor.getEditors();
    const editor = editors.find((item) => item.getModel()?.uri.path.endsWith("/c.txt"))!;
    return {
      ids: editors.map((item) => item.getId()),
      id: editor.getId(),
      position: editor.getPosition(),
    };
  });
  expect(initial).toContain(atEnd.id);
  expect(atEnd.position).toEqual({ lineNumber: 1, column: 1 });
  await scrollReview(page, "start");
  await expect(first).toBeVisible();
  await expect(first.locator(".find-widget.visible")).toHaveCount(0);
  const returned = await page.evaluate(() => {
    const editors = window.__WEAVIE_MONACO__!.editor.getEditors();
    const editor = editors.find((item) => item.getModel()?.uri.path.endsWith("/a.txt"))!;
    return { ids: editors.map((item) => item.getId()), position: editor.getPosition() };
  });
  expect(returned.position).toEqual({ lineNumber: 1, column: 1 });
  expect(returned.ids.sort()).toEqual(atEnd.ids.sort());
  expect(initial.every((id) => returned.ids.includes(id))).toBe(true);
  expect(
    await first.evaluate((element) =>
      element.parentElement!.style.getPropertyValue("--editor-font-size"),
    ),
  ).toBe("16px");
  await page.locator(".editor-tab.active .editor-tab-close").click();
  await expect(page.locator(".unified-review-file")).toHaveCount(0);
  const remaining = await page.evaluate(() =>
    window.__WEAVIE_MONACO__!.editor.getEditors().map((item) => item.getId()),
  );
  expect(remaining.sort()).toEqual(existing.sort());
});
