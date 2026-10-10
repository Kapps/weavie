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

test("review navigation publishes the latest editor height after delayed measurements", async ({
  page,
}) => {
  await awaitReviewSet(page, paths);
  await page.locator(".editor-empty-review").click();
  const target = paths.at(-1)!;
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: target }),
  });
  await expect(section).toHaveCount(0);
  const gate = await page.evaluateHandle((path) => {
    const NativeObserver = window.ResizeObserver;
    const pending = new Map<ResizeObserver, Map<Element, ResizeObserverEntry>>();
    const callbacks = new Map<ResizeObserver, ResizeObserverCallback>();
    let held = true;
    window.ResizeObserver = class extends NativeObserver {
      constructor(callback: ResizeObserverCallback) {
        super((entries, observer) => {
          const deferred: ResizeObserverEntry[] = [];
          const immediate: ResizeObserverEntry[] = [];
          for (const entry of entries) {
            const owner = entry.target.closest(".unified-review-file");
            const name = owner?.querySelector(".unified-review-file-name")?.textContent;
            (held && name === path ? deferred : immediate).push(entry);
          }
          if (deferred.length > 0) {
            const latest = pending.get(observer) ?? new Map<Element, ResizeObserverEntry>();
            for (const entry of deferred) latest.set(entry.target, entry);
            pending.set(observer, latest);
          }
          if (immediate.length > 0) callback(immediate, observer);
        });
        callbacks.set(this, callback);
      }
      override disconnect(): void {
        pending.delete(this);
        callbacks.delete(this);
        super.disconnect();
      }
    };
    return {
      bodyHeight: () =>
        [...pending.values()]
          .flatMap((entries) => [...entries.values()])
          .find((entry) => entry.target.matches(".unified-review-editor"))?.contentRect.height,
      release: () => {
        held = false;
        for (const [observer, entries] of pending) {
          callbacks.get(observer)!([...entries.values()], observer);
        }
        pending.clear();
        window.ResizeObserver = NativeObserver;
      },
    };
  }, target);
  await page.locator(".unified-review-tree-row.file", { hasText: target }).click();
  await expect(section.locator(".weavie-inline-removed-content")).toContainText("Value 901");
  await expect.poll(() => gate.evaluate((state) => state.bodyHeight())).toBeGreaterThan(0);
  const initialHeight = await gate.evaluate((state) => state.bodyHeight());
  await expect(section.locator(".monaco-editor")).not.toHaveClass(/focused/);
  await page.evaluate((path) => {
    const editor = window
      .__WEAVIE_MONACO__!.editor.getEditors()
      .find((candidate) => candidate.getModel()?.uri.path.endsWith(`/${path}`))!;
    editor.changeViewZones((accessor) => {
      accessor.addZone({
        afterLineNumber: 905,
        heightInPx: 40,
        domNode: document.createElement("div"),
      });
    });
  }, target);
  await expect.poll(() => gate.evaluate((state) => state.bodyHeight())).toBe(initialHeight! + 40);
  await gate.evaluate((state) => state.release());
  await expect(section.locator(".monaco-editor")).toHaveClass(/focused/);
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
    const codeLenses: import("monaco-editor").languages.CodeLensProvider = {
      onDidChange: (listener) => {
        const refresh = () => listener(codeLenses);
        listeners.add(refresh);
        return { dispose: () => listeners.delete(refresh) };
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
    };
    monaco.languages.registerCodeLensProvider("scroll-lenses", codeLenses);
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
  const caretLine = section.locator(".view-line", { hasText: /^Changed\sValue\s915$/ });
  await caretLine.click({ position: { x: 20, y: 8 } });
  await expect(section.locator(".monaco-editor")).toHaveClass(/focused/);
  const beforeWheel = (await reviewScroll(page)).top;
  await section.locator(".unified-review-file-header").hover();
  await page.clock.install();
  await page.clock.pauseAt(new Date((await page.evaluate(() => Date.now())) + 1_000));
  for (let step = 0; step < 14; step++) await page.mouse.wheel(0, 120);
  await page.clock.runFor(350);
  await page.clock.resume();
  await expect
    .poll(async () => (await reviewScroll(page)).top - beforeWheel)
    .toBeGreaterThanOrEqual(700);
  await expect(caretLine).not.toBeInViewport();
  await expect(section.locator(".monaco-editor")).toHaveClass(/focused/);
  const beforeFocusedRefresh = (await reviewScroll(page)).top;
  await observation.evaluate((state) => state.clear());
  await provider.evaluate((state) => state.refresh());
  await expect(section.locator(".codelens-decoration")).toHaveCount(3);
  expect((await reviewScroll(page)).top).toBe(beforeFocusedRefresh);
  expect(
    (await observation.evaluate((state) => state.positions)).every(
      (position) => position === beforeFocusedRefresh,
    ),
  ).toBe(true);
  await observation.evaluate((state) => state.dispose());
});
