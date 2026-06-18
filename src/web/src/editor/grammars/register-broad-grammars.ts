// Registers broad syntax highlighting for ~200 languages from the data-driven catalog. For each language
// we synthesize a VS Code extension manifest (language id + extensions + grammar + shared generic config)
// and register it the same way the @codingame default-extension packs do — `registerExtension` +
// `registerFileUrl` pointing at the bundled grammar asset. Contributions are declarative (no `main`, no
// extension-host JS); the grammar JSON is fetched lazily from the local bundle on first open of the
// language. Run once, after the VS Code services are initialized, alongside the curated pack imports.

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
 * `initialize(...)` (the languages/textmate services must exist) and before files are opened (Monaco
 * resolves a model's language from its extension at creation time, so the associations must be eager).
 */
export function registerBroadGrammars(): void {
  if (registered) {
    return;
  }
  registered = true;

  for (const grammar of buildBroadCatalog()) {
    const grammarUrl = grammarUrlByName[grammar.name];
    if (grammarUrl === undefined) {
      continue; // catalog entry without a bundled grammar file (shouldn't happen) -> skip
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
      // Declarative-only: no `main`, so no extension-host JS runs — just the grammar/language contributions.
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
