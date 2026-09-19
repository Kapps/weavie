import { beforeEach, expect, it, vi } from "vitest";

let receiveTheme: (value: unknown) => void;
const properties = new Map<string, string>();
vi.mock("../bridge", () => ({
  hostInjected: (_key: string, _value: unknown, dev: unknown) => dev,
  registerHostFeature: (install: (connection: unknown) => void) =>
    install({
      isLocal: true,
      host: {
        feature: () => ({
          on: (_event: string, handler: typeof receiveTheme) => {
            receiveTheme = handler;
          },
        }),
      },
    }),
}));

beforeEach(() => {
  vi.resetModules();
  properties.clear();
  vi.stubGlobal("window", {});
  vi.stubGlobal("document", {
    documentElement: {
      dataset: {},
      style: {
        setProperty: (key: string, value: string) => properties.set(key, value),
        removeProperty: (key: string) => properties.delete(key),
      },
    },
    querySelector: () => null,
  });
});

it("cancels onto the latest host state and removes preview-only colors", async () => {
  const controller = await import("./controller");
  const preview = controller.beginThemePreview();
  preview.show({
    id: "temporary",
    theme: {
      name: "Temporary",
      type: "light",
      colors: { "editor.background": "#123456", "preview.only": "#abcdef" },
      tokenColors: [],
    },
  });
  expect(properties.get("--bg")).toBe("#123456");
  receiveTheme({ mode: "dark", light: { id: "weavie-light" }, dark: { id: "weavie-dark" } });
  expect(properties.get("--bg")).toBe("#123456");
  preview.dispose();
  expect(controller.currentThemeId()).toBe("weavie-dark");
  expect(properties.get("--bg")).not.toBe("#123456");
  expect(properties.has("--weavie-preview-only")).toBe(false);
  preview.show({ id: "weavie-light" });
  expect(controller.currentMonacoTheme().theme.type).toBe("dark");
});
