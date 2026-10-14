import { beforeEach, expect, it, vi } from "vitest";

let receiveTheme: (value: unknown) => void;
const properties = new Map<string, string>();
const editorProperties = new Map<string, string>();
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
  editorProperties.clear();
  const editorStyle = {
    set cssText(_: string) {
      editorProperties.clear();
    },
    setProperty: (key: string, value: string) => editorProperties.set(key, value),
  };
  vi.stubGlobal("window", {});
  vi.stubGlobal("document", {
    createElement: () => ({ sheet: { cssRules: [{ style: editorStyle }] } }),
    head: { append: () => {} },
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
  const preview = controller.beginThemePreview("app");
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

it("themes editor surfaces from the editor slot and the chrome from the app slot", async () => {
  const controller = await import("./controller");
  const editorTheme = {
    name: "Editor",
    type: "light" as const,
    colors: { "editor.background": "#fafafa" },
    tokenColors: [],
  };
  receiveTheme({
    mode: "dark",
    light: { id: "weavie-light" },
    dark: { id: "weavie-dark" },
    editorDark: { id: "editor", theme: editorTheme },
  });
  expect(properties.get("--bg")).not.toBe("#fafafa");
  expect(editorProperties.get("--bg")).toBe("#fafafa");
  expect(editorProperties.get("--weavie-editor-background")).toBe("#fafafa");
  expect(editorProperties.get("color-scheme")).toBe("light");
  expect(editorProperties.get("--diff-added-line")).toBe("initial");
  expect(controller.currentMonacoTheme().theme.type).toBe("light");
  expect(controller.currentXtermTheme().background).not.toBe("#fafafa");
});

it("previews an editor theme on the editor surfaces only", async () => {
  const controller = await import("./controller");
  const preview = controller.beginThemePreview("editor");
  preview.show({
    id: "temporary",
    theme: {
      name: "Temporary",
      type: "light",
      colors: { "editor.background": "#fdfdfd" },
      tokenColors: [],
    },
  });
  expect(editorProperties.get("--bg")).toBe("#fdfdfd");
  expect(properties.get("--bg")).not.toBe("#fdfdfd");
  expect(controller.currentMonacoTheme().theme.type).toBe("light");
  expect(controller.currentEditorThemeId()).toBeUndefined();
  preview.dispose();
  expect(editorProperties.get("--bg")).toBe(properties.get("--bg"));
});
