import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const paths = ["a.paint", "b.paint", "c.paint"] as const;
const baseline = Array.from({ length: 2_000 }, (_, index) => `Value ${index + 1}`);
const changed = baseline.map((line, index) =>
  (index >= 500 && index < 550) || (index >= 1_500 && index < 1_550) ? `Changed ${line}` : line,
);

test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await Promise.all(paths.map((path) => writeFile(join(workspace, path), baseline.join("\n"))));
    },
  },
  fakeScript: { steps: paths.flatMap((path) => appliedEdit(path, changed.join("\n"))) },
});

test("each review mount paints its hunks once and real diff changes repaint", async ({
  page,
  weavie,
}) => {
  await awaitReviewSet(page, [...paths]);
  const observation = await page.evaluateHandle(() => {
    const samples: { path: string; disposed: boolean; additions: Record<string, number> }[] = [];
    const subscription = window.__WEAVIE_MONACO__!.editor.onDidCreateEditor((editor) => {
      const sample = { path: "", disposed: false, additions: {} as Record<string, number> };
      samples.push(sample);
      const add = editor.addContentWidget;
      editor.addContentWidget = (widget) => {
        const id = widget.getId().match(/^weavie\.(pending|accepted)\.\d+/)?.[0];
        if (id !== undefined) {
          sample.path = editor.getModel()!.uri.path;
          sample.additions[id] = (sample.additions[id] ?? 0) + 1;
        }
        add.call(editor, widget);
      };
      editor.onDidDispose(() => {
        sample.disposed = true;
      });
    });
    return {
      read: (path: string) => samples.filter((sample) => sample.path.endsWith(`/${path}`)),
      dispose: () => subscription.dispose(),
    };
  });
  await page.locator(".editor-empty-review").click();
  const first = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: paths[0] }),
  });
  const firstRow = page.locator(".unified-review-tree-row.file", { hasText: paths[0] });
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  const once = { "weavie.pending.0": 1, "weavie.pending.1": 1 };

  await firstRow.click();
  await expect(first.locator(".monaco-editor")).toHaveClass(/focused/);
  await expect(first.locator(".weavie-inline-pending-tag")).toHaveCount(2);
  const initial = await observation.evaluate((state) => state.read("a.paint"));
  expect(initial).toHaveLength(1);
  expect(initial[0]!.additions).toEqual(once);

  await scrollbar.press("End");
  await expect(first.locator(".monaco-editor")).toHaveCount(0);
  await expect
    .poll(() => observation.evaluate((state) => state.read("a.paint")[0]!.disposed))
    .toBe(true);
  await scrollbar.press("Home");
  await firstRow.click();
  await expect(first.locator(".monaco-editor")).toHaveClass(/focused/);
  await expect(first.locator(".weavie-inline-pending-tag")).toHaveCount(2);
  const remounted = await observation.evaluate((state) => state.read("a.paint"));
  expect(remounted).toHaveLength(2);
  expect(remounted[1]!.additions).toEqual(once);

  const updated = [...changed];
  updated[1_000] = "Changed Value 1001";
  await writeFile(join(weavie.workspace, paths[0]), updated.join("\n"));
  await expect(first.locator(".weavie-inline-pending-tag")).toHaveCount(3);
  const repainted = await observation.evaluate((state) => state.read("a.paint"));
  expect(repainted).toHaveLength(2);
  expect(repainted[1]!.additions).toEqual({
    "weavie.pending.0": 2,
    "weavie.pending.1": 2,
    "weavie.pending.2": 1,
  });
  await observation.evaluate((state) => state.dispose());
});
