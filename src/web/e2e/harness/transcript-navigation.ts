import { expect, type Locator } from "@playwright/test";
import { transcriptGeometry } from "./transcript-geometry";

export interface TranscriptRowSnapshot {
  entryId: string;
  index: number;
  texts: string[];
}

async function settleTranscript(body: Locator): Promise<void> {
  await expect
    .poll(() =>
      body.evaluate(async (element) => {
        const rows = element.querySelector<HTMLElement>(".monaco-list-rows");
        if (rows === null) throw new Error("Transcript rows are unavailable");
        const before = `${rows.style.top}/${rows.style.height}`;
        await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
        await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
        return before === `${rows.style.top}/${rows.style.height}`;
      }),
    )
    .toBe(true);
}

export async function revealTranscriptTarget(surface: Locator, target: Locator): Promise<void> {
  const body = surface.locator(".agent-body");
  const viewport = body.locator(":scope > .monaco-list");
  await viewport.focus();
  await viewport.press("Home");
  for (;;) {
    await settleTranscript(body);
    if ((await target.count()) > 0) {
      const position = await target.evaluate((element) => {
        const viewport = element.closest(".agent-body");
        if (viewport === null) throw new Error("Target is outside the transcript");
        const bounds = viewport.getBoundingClientRect();
        const target = element.getBoundingClientRect();
        const intersects = target.bottom > bounds.top && target.top < bounds.bottom;
        return {
          visible:
            intersects &&
            (target.height > bounds.height ||
              (target.top >= bounds.top && target.bottom <= bounds.bottom)),
          intersects,
          correction:
            target.top < bounds.top ? target.top - bounds.top : target.bottom - bounds.bottom,
        };
      });
      if (position.visible) return;
      if (position.intersects) {
        const point = await body.evaluate((element) => {
          const bounds = element.getBoundingClientRect();
          const point = {
            x: bounds.right - Math.min(12, bounds.width / 2),
            y: bounds.top + bounds.height / 2,
          };
          if (!element.contains(document.elementFromPoint(point.x, point.y)))
            throw new Error("Transcript wheel point is covered by another surface");
          return point;
        });
        await surface.page().mouse.move(point.x, point.y);
        await surface.page().mouse.wheel(0, position.correction);
        continue;
      }
    }
    const before = await body.evaluate(transcriptGeometry);
    if (before.bottomDistance <= 1) {
      await expect(target).toBeInViewport();
      return;
    }
    await viewport.press("PageDown");
  }
}

export async function collectTranscriptRows(
  surface: Locator,
  selector: string,
): Promise<TranscriptRowSnapshot[]> {
  const body = surface.locator(".agent-body");
  const viewport = body.locator(":scope > .monaco-list");
  const collected = new Map<string, TranscriptRowSnapshot>();
  const entryAtIndex = new Map<number, string>();
  let total: number | null = null;
  await viewport.focus();
  await viewport.press("Home");
  for (;;) {
    await settleTranscript(body);
    const rows = await body.locator(".agent-virtual-row").evaluateAll(
      (elements, selector) =>
        elements.map((element) => ({
          entryId: element.getAttribute("data-transcript-entry"),
          index: Number.parseInt(element.getAttribute("data-index") ?? "", 10),
          total: Number.parseInt(
            element.closest(".monaco-list-row")?.getAttribute("aria-setsize") ?? "",
            10,
          ),
          texts: [...element.querySelectorAll(selector)].map(
            (content) => content.textContent ?? "",
          ),
        })),
      selector,
    );
    expect(new Set(rows.map((row) => row.entryId)).size).toBe(rows.length);
    for (const row of rows) {
      if (row.entryId === null || row.entryId === "")
        throw new Error("Transcript row has no identity");
      expect(Number.isInteger(row.index), "Transcript row index must be valid").toBe(true);
      expect(Number.isInteger(row.total), "Widget must expose its total row count").toBe(true);
      expect(row.index).toBeGreaterThanOrEqual(0);
      expect(row.index).toBeLessThan(row.total);
      total ??= row.total;
      expect(row.total, "Transcript must remain stable while its history is collected").toBe(total);
      const previous = collected.get(row.entryId);
      if (previous !== undefined)
        expect(row.index, "An identity cannot occupy multiple indices").toBe(previous.index);
      const identity = entryAtIndex.get(row.index);
      if (identity !== undefined)
        expect(row.entryId, "An index cannot contain multiple identities").toBe(identity);
      entryAtIndex.set(row.index, row.entryId);
      collected.set(row.entryId, { entryId: row.entryId, index: row.index, texts: row.texts });
    }
    if ((await body.evaluate(transcriptGeometry)).bottomDistance <= 1) break;
    await viewport.press("PageDown");
  }
  const history = [...collected.values()].sort((left, right) => left.index - right.index);
  if (total === null) {
    expect((await body.evaluate(transcriptGeometry)).contentHeight).toBe(0);
  } else {
    expect(
      history.map((row) => row.index),
      "Every transcript row must be inspected exactly once",
    ).toEqual(Array.from({ length: total }, (_, index) => index));
  }
  return history;
}
