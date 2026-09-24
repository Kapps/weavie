import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { awaitFontsSettled } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const paths = ["a.lensheight", "b.lensheight", "c.lensheight"] as const;
const baseline = Array.from({ length: 2_000 }, (_, index) => `Value ${index + 1}`);
const changed = baseline.map((line, index) =>
  index >= 900 && index < 950 ? `Changed ${line}` : line,
);

test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await Promise.all(paths.map((path) => writeFile(join(workspace, path), baseline.join("\n"))));
    },
  },
  fakeScript: { steps: paths.flatMap((path) => appliedEdit(path, changed.join("\n"))) },
});

test("cached CodeLens remounts preserve published section height without recreating the editor", async ({
  page,
}) => {
  await awaitReviewSet(page, [...paths]);
  await awaitFontsSettled(page);
  const observation = await page.evaluateHandle((target) => {
    const monaco = window.__WEAVIE_MONACO__!;
    monaco.languages.register({ id: "lens-height", extensions: [".lensheight"] });
    const provider = monaco.languages.registerCodeLensProvider("lens-height", {
      provideCodeLenses: () => ({
        lenses: [905, 910, 915].map((line) => ({
          range: { startLineNumber: line, startColumn: 1, endLineNumber: line, endColumn: 1 },
          command: { id: "", title: `Stable lens ${line}` },
        })),
        dispose() {},
      }),
    });
    const samples: { kind: "body" | "section"; height: number }[] = [];
    const styles: { before: string | null; after: string | null }[] = [];
    let tracking = false;
    let mounts = 0;
    const created = monaco.editor.onDidCreateEditor((editor) => {
      const modelChanged = editor.onDidChangeModel(() => {
        if (editor.getModel()?.uri.path.endsWith(`/${target}`)) mounts++;
      });
      editor.onDidDispose(() => modelChanged.dispose());
    });
    const owns = (element: Element) =>
      element.closest(".unified-review-file")?.querySelector(".unified-review-file-name")
        ?.textContent === target;
    const NativeObserver = window.ResizeObserver;
    window.ResizeObserver = class extends NativeObserver {
      constructor(callback: ResizeObserverCallback) {
        super((entries, observer) => {
          if (tracking) {
            for (const entry of entries) {
              if (!owns(entry.target)) continue;
              if (entry.target.matches(".unified-review-editor")) {
                samples.push({ kind: "body", height: entry.contentRect.height });
              } else if (entry.target.matches(".unified-review-file")) {
                samples.push({ kind: "section", height: entry.borderBoxSize[0]!.blockSize });
              }
            }
          }
          callback(entries, observer);
        });
      }
    };
    const mutations = new MutationObserver((records) => {
      if (!tracking) return;
      for (const record of records) {
        const element = record.target;
        if (
          element instanceof Element &&
          element.matches(".unified-review-editor") &&
          owns(element)
        ) {
          styles.push({ before: record.oldValue, after: element.getAttribute("style") });
        }
      }
    });
    mutations.observe(document.documentElement, {
      subtree: true,
      attributes: true,
      attributeFilter: ["style"],
      attributeOldValue: true,
    });
    return {
      start: () => {
        tracking = true;
        return mounts;
      },
      read: () => ({ samples, styles, mounts }),
      dispose: () => {
        tracking = false;
        mutations.disconnect();
        window.ResizeObserver = NativeObserver;
        created.dispose();
        provider.dispose();
      },
    };
  }, paths[0]);
  await page.locator(".editor-empty-review").click();
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  const first = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: paths[0] }),
  });
  let cached = { body: 0, section: 0 };
  for (const path of [...paths, paths[0]]) {
    await scrollbar.press("Home");
    await page.locator(".unified-review-tree-row.file", { hasText: path }).click();
    const section = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: path }),
    });
    await expect(section.locator(".codelens-decoration")).toHaveCount(3);
    const measured = await section.evaluate(
      (element) =>
        new Promise<{ body: number; section: number }>((resolve) => {
          const body = element.querySelector(".unified-review-editor")!;
          const observer = new ResizeObserver((entries) => {
            const bodyEntry = entries.find((entry) => entry.target === body);
            const sectionEntry = entries.find((entry) => entry.target === element);
            if (bodyEntry === undefined || sectionEntry === undefined) return;
            observer.disconnect();
            resolve({
              body: bodyEntry.contentRect.height,
              section: sectionEntry.borderBoxSize[0]!.blockSize,
            });
          });
          observer.observe(body);
          observer.observe(element, { box: "border-box" });
        }),
    );
    if (path === paths[0]) cached = measured;
  }
  await scrollbar.press("End");
  await expect(first.locator(".monaco-editor")).toHaveCount(0);
  const previousMounts = await observation.evaluate((state) => state.start());
  await scrollbar.press("Home");
  await page.locator(".unified-review-tree-row.file", { hasText: paths[0] }).click();
  await expect(first.locator(".codelens-decoration")).toHaveCount(3);
  await expect(first.locator(".monaco-editor")).toHaveClass(/focused/);
  await expect
    .poll(() =>
      observation.evaluate((state) =>
        state.read().samples.some((sample) => sample.kind === "body"),
      ),
    )
    .toBe(true);
  const result = await observation.evaluate((state) => state.read());
  await test.info().attach("cached-codelens-geometry", {
    body: JSON.stringify({ cached, previousMounts, ...result }, null, 2),
    contentType: "application/json",
  });
  expect(result.samples.some((sample) => sample.kind === "section")).toBe(true);
  for (const sample of result.samples) expect(sample.height).toBe(cached[sample.kind]);
  expect(result.mounts).toBe(previousMounts + 1);
  await observation.evaluate((state) => state.dispose());
});
