// Registers and applies a custom VS Code color theme to Monaco through @codingame/monaco-vscode-api.
//
// IMPORTANT: monaco.editor.defineTheme is a no-op here. With getThemeServiceOverride() installed, the theme
// service is the full VS Code WorkbenchThemeService, which has no defineTheme — so a theme must be
// contributed as a declarative extension, exactly how the built-in themes load. The in-code theme JSON is
// served via a data: URL that the file service fetches, and `id` becomes the theme's settingsId (the string
// setTheme expects). registerExtension is async, so await whenReady() before setTheme; otherwise the theme
// isn't in the registry yet and setTheme silently no-ops.
//
// Pulls in monaco + the vscode-api extension surface, so imported ONLY from the editor chunk
// (vscode-services.ts), never from the monaco-free theme controller or the entry path.

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

  // registerExtension's return is a union (local vs remote ext-host); registerFileUrl exists only on the
  // local variant, which a declarative themes-only extension always is. Narrow to the members used rather
  // than depend on the unexported concrete result type.
  const extension = registerExtension(manifest, undefined, { system: true }) as {
    registerFileUrl?: (path: string, url: string) => unknown;
    whenReady: () => Promise<void>;
  };
  // semanticHighlighting must be truthy for semanticTokenColors to paint; force it on regardless of what
  // the theme JSON carried.
  const json = JSON.stringify({ ...theme, semanticHighlighting: true });
  // The file service fetch()es this URL; the data: URL carries the application/json mime type.
  const url = `data:application/json;charset=utf-8,${encodeURIComponent(json)}`;
  extension.registerFileUrl?.("./theme.json", url);

  const ready = extension.whenReady();
  registered.set(id, ready);
  return ready;
}

/**
 * Registers (if new) and activates the given theme. The controller bumps the id on every active-theme or
 * override change, so re-applying registers a fresh contribution and switches to it — the live-reapply path,
 * since the theme service can't mutate a registered theme in place.
 */
export async function applyMonacoTheme(id: string, theme: VsCodeColorTheme): Promise<void> {
  await register(id, theme);
  monaco.editor.setTheme(id);
}
