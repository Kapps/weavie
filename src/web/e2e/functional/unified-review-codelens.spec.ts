import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import { reviewScroll } from "../harness/review-scroll";

const source = Array.from({ length: 2_000 }, (_, index) => `Value ${index + 1}`);
const paths = Array.from(
  { length: 12 },
  (_, index) => `lens-${String(index).padStart(2, "0")}.scrolllens`,
);
test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await Promise.all(paths.map((path) => writeFile(join(workspace, path), source.join("\n"))));
    },
  },
  fakeScript: {
    steps: paths.flatMap((path) =>
      appliedEdit(
        path,
        source
          .map((line, index) => (index >= 900 && index < 950 ? `Changed ${line}` : line))
          .join("\n"),
      ),
    ),
  },
});

test("background CodeLens refreshes cannot take scrolling ownership after review remounts", async ({
  page,
}) => {
  await awaitReviewSet(page, paths);
  const provider = await page.evaluateHandle(() => {
    const monaco = window.__WEAVIE_MONACO__!;
    monaco.languages.register({ id: "scroll-lenses", extensions: [".scrolllens"] });
    const listeners = new Set<() => void>();
    const requests: string[] = [];
    let generation = 0;
    monaco.languages.registerCodeLensProvider("scroll-lenses", {
      onDidChange: (listener) => {
        listeners.add(listener);
        return { dispose: () => listeners.delete(listener) };
      },
      provideCodeLenses: (model) => {
        requests.push(model.uri.path);
        return {
          lenses: (generation % 2 === 0 ? [905] : [905, 910, 915]).map((line) => ({
            range: { startLineNumber: line, startColumn: 1, endLineNumber: line, endColumn: 1 },
            command: { id: "", title: `Lens generation ${generation}` },
          })),
          dispose() {},
        };
      },
    });
    return {
      requests,
      refresh: () => {
        generation++;
        for (const listener of listeners) listener();
        return generation;
      },
    };
  });
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: paths[0]! }),
  });
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  await page.locator(".unified-review-tree-row.file").first().click();
  await expect(section.locator(".codelens-decoration")).toContainText(["Lens generation 0"]);
  await scrollbar.press("End");
  await expect(section.locator(".monaco-editor")).toHaveCount(0);
  await expect
    .poll(() =>
      provider.evaluate((state) =>
        state.requests.some((path) => path.endsWith("lens-11.scrolllens")),
      ),
    )
    .toBe(true);
  await scrollbar.press("Home");
  await page.locator(".unified-review-tree-row.file").first().click();
  await expect(section.locator(".codelens-decoration")).toContainText(["Lens generation 0"]);
  for (let step = 0; step < 12; step++) await scrollbar.press("ArrowDown");

  const observation = await scrollbar.evaluateHandle((element) => {
    const positions: number[] = [];
    const observer = new MutationObserver(() =>
      positions.push(Number(element.getAttribute("aria-valuenow"))),
    );
    observer.observe(element, { attributes: true, attributeFilter: ["aria-valuenow"] });
    return {
      positions,
      clear: () => {
        positions.length = 0;
      },
      dispose: () => observer.disconnect(),
    };
  });
  for (const direction of [-1, 1]) {
    const before = (await reviewScroll(page)).top;
    const generation = await provider.evaluate((state) => state.refresh());
    await expect(section.locator(".codelens-decoration")).toHaveCount(generation % 2 === 0 ? 1 : 3);
    expect((await reviewScroll(page)).top).toBe(before);
    expect(
      await observation.evaluate((state) =>
        state.positions.every((position) => position === state.positions[0]),
      ),
    ).toBe(true);

    await observation.evaluate((state) => state.clear());
    await section.locator(".unified-review-file-header").hover();
    await page.mouse.wheel(0, direction * 120);
    await expect
      .poll(async () => ((await reviewScroll(page)).top - before) * direction)
      .toBeGreaterThan(0);
    await expect
      .poll(async () => Math.abs((await reviewScroll(page)).top - before))
      .toBeGreaterThanOrEqual(50);
    const positions = await observation.evaluate((state) => state.positions);
    expect(
      Math.max(...positions.map((position) => Math.abs(position - before))),
    ).toBeLessThanOrEqual(120);
    expect(
      positions.every(
        (position, index) => index === 0 || (position - positions[index - 1]!) * direction >= 0,
      ),
    ).toBe(true);
    const viewportHeight = await page
      .locator(".unified-review-diffs")
      .evaluate((element) => element.clientHeight);
    expect(
      await section.locator(".monaco-editor").evaluate((element) => element.clientHeight),
    ).toBeLessThanOrEqual(viewportHeight);
    await observation.evaluate((state) => state.clear());
  }
  await observation.evaluate((state) => state.dispose());
});
