import type { IConfigurationService } from "@codingame/monaco-vscode-api/vscode/vs/platform/configuration/common/configuration.service";
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
      _configurationService: IConfigurationService;
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

test("equal parent and child overrides retain their explicit removal semantics", async ({
  page,
}) => {
  await page.locator(".editor-empty-review").click();
  await expect(page.locator(".unified-review-file .monaco-editor")).toBeVisible();
  const results = await page.evaluate(async () => {
    const service = (
      window.__WEAVIE_EDITOR__ as unknown as {
        _configurationService: IConfigurationService;
      }
    )._configurationService;
    const parent = "editor.minimap";
    const child = `${parent}.enabled`;
    const defaults = service.inspect<boolean>(child).defaultValue!;
    const results = [];
    for (const order of ["parent-first", "child-first"]) {
      await service.updateValue(child, undefined);
      await service.updateValue(parent, undefined);
      if (order === "parent-first") {
        await service.updateValue(parent, { enabled: !defaults });
        await service.updateValue(child, !defaults);
        await service.updateValue(child, undefined);
      } else {
        await service.updateValue(child, !defaults);
        await service.updateValue(parent, { enabled: !defaults });
        await service.updateValue(parent, undefined);
      }
      results.push({ order, defaults, actual: service.getValue<boolean>(child) });
    }
    return results;
  });
  for (const result of results) expect(result.actual, result.order).toBe(result.defaults);
});

test("configuration owns snapshots of mutable object and array options", async ({ page }) => {
  await page.locator(".editor-empty-review").click();
  await expect(page.locator(".unified-review-file .monaco-editor")).toBeVisible();
  const result = await page.evaluate(async () => {
    const service = (
      window.__WEAVIE_EDITOR__ as unknown as {
        _configurationService: IConfigurationService;
      }
    )._configurationService;
    const minimap = { enabled: false };
    const rulers = [{ column: 80, color: "#ffffff" }];
    await service.updateValue("editor.minimap", minimap);
    await service.updateValue("editor.rulers", rulers);
    const initial = {
      minimap: service.getValue<boolean>("editor.minimap.enabled"),
      rulers: service.getValue("editor.rulers"),
    };
    minimap.enabled = true;
    rulers[0]!.column = 100;
    const stored = {
      minimap: service.inspect("editor.minimap").memoryValue,
      rulers: service.inspect("editor.rulers").memoryValue,
    };
    await service.updateValue("editor.minimap", minimap);
    await service.updateValue("editor.rulers", rulers);
    return {
      initial,
      stored,
      updated: {
        minimap: service.getValue<boolean>("editor.minimap.enabled"),
        rulers: service.getValue("editor.rulers"),
      },
    };
  });
  expect(result.initial).toEqual({ minimap: false, rulers: [{ column: 80, color: "#ffffff" }] });
  expect(result.stored).toEqual({
    minimap: { enabled: false },
    rulers: [{ column: 80, color: "#ffffff" }],
  });
  expect(result.updated).toEqual({ minimap: true, rulers: [{ column: 100, color: "#ffffff" }] });
});
