// Registers broad syntax highlighting for ~200 languages from the catalog. For each, synthesize a VS Code
// extension manifest (language id + extensions + grammar + shared generic config) and register it via
// `registerExtension` + `registerFileUrl` pointing at the bundled grammar asset. Contributions are
// declarative (no extension-host JS); each grammar JSON is fetched lazily from the bundle on first open.

import {
  ExtensionHostKind,
  type IExtensionManifest,
  registerExtension,
} from "@codingame/monaco-vscode-api/extensions";
import { GENERIC_CONFIGURATION_PATH, genericConfigurationUrl } from "./generic-config";
import { grammarUrlByName } from "./grammar-assets";
import { buildBroadCatalog } from "./grammar-catalog";

type RegisterFileUrl = (
  path: string,
  url: string,
  metadata?: { mimeType?: string; size?: number },
) => unknown;

let registered = false;

/**
 * Registers every catalog language's grammar + generic config with Monaco. Idempotent. Must run after
 * `initialize(...)` and before files are opened, since Monaco resolves a model's language from its extension
 * at creation time so the associations must be eager.
 */
export function registerBroadGrammars(): void {
  if (registered) {
    return;
  }
  registered = true;

  for (const grammar of buildBroadCatalog()) {
    const grammarUrl = grammarUrlByName[grammar.name];
    if (grammarUrl === undefined) {
      continue; // catalog entry without a bundled grammar file (shouldn't happen)
    }

    const grammarPath = `./syntaxes/${grammar.name}.tmLanguage.json`;
    const manifest = {
      name: `weavie-tm-${grammar.name}`,
      displayName: grammar.displayName,
      version: "1.0.0",
      publisher: "weavie",
      engines: { vscode: "*" },
      categories: ["Programming Languages"],
      contributes: {
        languages: [
          {
            id: grammar.name,
            extensions: [...grammar.extensions],
            configuration: GENERIC_CONFIGURATION_PATH,
          },
        ],
        grammars: [{ language: grammar.name, scopeName: grammar.scopeName, path: grammarPath }],
      },
      // Declarative-only: no `main`, so no extension-host JS runs.
    } as unknown as IExtensionManifest;

    const registration = registerExtension(manifest, ExtensionHostKind.LocalWebWorker, {
      system: true,
    });
    const registerFileUrl = registration.registerFileUrl as RegisterFileUrl;
    registerFileUrl(grammarPath, grammarUrl, { mimeType: "application/json" });
    registerFileUrl(GENERIC_CONFIGURATION_PATH, genericConfigurationUrl, {
      mimeType: "application/json",
    });
  }
}
