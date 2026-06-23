// Registers and applies a custom VS Code color theme to Monaco through @codingame/monaco-vscode-api.
//
// IMPORTANT: monaco.editor.defineTheme is a no-op with getThemeServiceOverride() installed (the full
// WorkbenchThemeService has none), so a theme must be contributed as a declarative extension served via a
// data: URL, with `id` becoming its settingsId. registerExtension is async — await whenReady() before
// setTheme, or the theme isn't registered yet and setTheme silently no-ops.
//
// Pulls in monaco + the vscode-api extension surface, so imported ONLY from the editor chunk, never from
// the monaco-free theme controller or the entry path.

import { registerExtension } from "@codingame/monaco-vscode-api/extensions";
import * as monaco from "monaco-editor";
import type { VsCodeColorTheme } from "./vscode-theme";

const registered = new Map<string, Promise<void>>();

type UiTheme = "vs" | "vs-dark" | "hc-black" | "hc-light";

function uiThemeFor(type: VsCodeColorTheme["type"]): UiTheme {
  switch (type) {
    case "light":
      return "vs";
    case "hcLight":
      return "hc-light";
    case "hc":
      return "hc-black";
    default:
      return "vs-dark";
  }
}

function register(id: string, theme: VsCodeColorTheme): Promise<void> {
  const existing = registered.get(id);
  if (existing !== undefined) {
    return existing;
  }

  const manifest = {
    name: `weavie-theme-${id.replace(/[^\w.-]+/g, "-")}`,
    publisher: "weavie",
    version: "1.0.0",
    engines: { vscode: "*" },
    contributes: {
      // `id` here is the theme's settingsId — the exact string passed to setTheme below.
      themes: [{ id, label: theme.name, uiTheme: uiThemeFor(theme.type), path: "./theme.json" }],
    },
  };

  // registerExtension's return is a union; registerFileUrl exists only on the local variant (always the case
  // for a declarative themes-only extension). Narrow to the members used, not the unexported result type.
  const extension = registerExtension(manifest, undefined, { system: true }) as {
    registerFileUrl?: (path: string, url: string) => unknown;
    whenReady: () => Promise<void>;
  };
  // semanticHighlighting must be truthy for semanticTokenColors to paint; force it on regardless of the JSON.
  const json = JSON.stringify({ ...theme, semanticHighlighting: true });
  // The file service fetch()es this URL; the data: URL carries the application/json mime type.
  const url = `data:application/json;charset=utf-8,${encodeURIComponent(json)}`;
  extension.registerFileUrl?.("./theme.json", url);

  const ready = extension.whenReady();
  registered.set(id, ready);
  return ready;
}

/**
 * Registers (if new) and activates the given theme. The controller bumps the id on every change, so
 * re-applying registers a fresh contribution and switches to it — the theme service can't mutate one in place.
 */
export async function applyMonacoTheme(id: string, theme: VsCodeColorTheme): Promise<void> {
  await register(id, theme);
  monaco.editor.setTheme(id);
}
