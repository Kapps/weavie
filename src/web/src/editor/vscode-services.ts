// Initializes the VSCode services that back Monaco, per the theming+LSP spec (§7): the *services /
// editor* slice of @codingame/monaco-vscode-api — theme + textmate + languages only. This is the
// layout-safe path: we pass no container to `initialize`, so no workbench/activity-bar/sidebar/panel
// is ever rendered (the spec's hard-no). It must run exactly once, before the first editor is created.
//
// Guardrail (spec §18): we deliberately import ONLY the theme/textmate/languages overrides — never a
// workbench/views/configuration/keybindings override, and never the extension host. The default VSCode
// "extensions" we import are declarative data only (grammars, language configs, theme JSON).

import { initialize } from "@codingame/monaco-vscode-api";
import getLanguagesServiceOverride from "@codingame/monaco-vscode-languages-service-override";
import getTextmateServiceOverride from "@codingame/monaco-vscode-textmate-service-override";
import getThemeServiceOverride from "@codingame/monaco-vscode-theme-service-override";

// Default VSCode extensions — declarative contributions, no extension-host JS:
//  - theme-defaults: the built-in Dark+/Light+/Modern color themes (so setTheme has something to load)
//  - typescript-basics: the TS/JS TextMate grammars + language configuration
import "@codingame/monaco-vscode-theme-defaults-default-extension";
import "@codingame/monaco-vscode-typescript-basics-default-extension";

import textMateWorker from "@codingame/monaco-vscode-textmate-service-override/worker?worker";
// Workers. monaco-vscode-api uses a generic editor worker for most services and a dedicated worker
// for TextMate background tokenization (label "TextMateWorker"). The `monaco-editor` specifier is
// aliased to @codingame/monaco-vscode-editor-api (see package.json), so this is the vscode editor worker.
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";

let initPromise: Promise<void> | undefined;

/** Initializes the VSCode services backing Monaco. Idempotent — subsequent calls return the same promise. */
export function initEditorServices(): Promise<void> {
  if (initPromise === undefined) {
    initPromise = doInit();
  }
  return initPromise;
}

async function doInit(): Promise<void> {
  self.MonacoEnvironment = {
    getWorker(_workerId: string, label: string): Worker {
      if (label === "TextMateWorker") {
        return new textMateWorker();
      }
      return new editorWorker();
    },
  };

  // No container argument → services/editor mode (no workbench, no layout control).
  await initialize({
    ...getThemeServiceOverride(),
    ...getTextmateServiceOverride(),
    ...getLanguagesServiceOverride(),
  });
}
