import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

test.use({ fakeScript: { steps: appliedEdit("configuration.txt", "changed") } });

test("equivalent partial editor configuration does not notify every cached model", async ({
  page,
}) => {
  await page.locator(".editor-empty-review").click();
  await expect(page.locator(".unified-review-file .monaco-editor")).toBeVisible();
  const result = await page.evaluate(async () => {
    const editor = window.__WEAVIE_EDITOR__ as unknown as {
      _configurationService: {
        inspect(key: string): { memoryValue: unknown; defaultValue: unknown };
        getValue(key: string): unknown;
        updateValue(key: string, value: unknown): Promise<void>;
        onDidChangeConfiguration(
          listener: (event: { affectedKeys: ReadonlySet<string> }) => void,
        ): { dispose(): void };
      };
    };
    const service = editor._configurationService;
    const key = "editor.minimap";
    const previous = service.inspect(key).memoryValue;
    await service.updateValue(key, undefined);
    let events = 0;
    const listener = service.onDidChangeConfiguration((event) => {
      if (event.affectedKeys.has(key)) events++;
    });
    const counts: number[] = [];
    const values: unknown[] = [];
    try {
      for (const value of [
        { enabled: true },
        { enabled: true },
        { enabled: false },
        undefined,
        undefined,
      ]) {
        await service.updateValue(key, value);
        counts.push(events);
        values.push(service.getValue(key));
      }
      await service.updateValue(key, { enabled: true });
      await service.updateValue(key, { enabled: true });
      await service.updateValue(`${key}.enabled`, false);
      const nested = service.getValue(`${key}.enabled`);
      await service.updateValue(`${key}.enabled`, undefined);
      const removed = service.getValue(key);
      return { counts, values, nested, removed, defaults: service.inspect(key).defaultValue };
    } finally {
      listener.dispose();
      await service.updateValue(key, previous);
    }
  });
  expect(result.counts).toEqual([1, 1, 2, 3, 3]);
  expect(result.values[0]).toMatchObject({ enabled: true });
  expect(result.values[2]).toMatchObject({ enabled: false });
  expect(result.values[3]).toEqual(result.defaults);
  expect(result.nested).toBe(false);
  expect(result.removed).toEqual(result.defaults);
});
