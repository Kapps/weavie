import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import { expect, it } from "vitest";
import { createOnigScanner, createOnigString, loadWASM } from "vscode-oniguruma";
import { parseRawGrammar, Registry } from "vscode-textmate";
import { ScopedModel } from "./spell-worker";

const require = createRequire(import.meta.url);
const registry = new Registry({
  onigLib: readFile(require.resolve("vscode-oniguruma/release/onig.wasm")).then(async (bytes) => {
    await loadWASM(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
    return { createOnigScanner, createOnigString };
  }),
  loadGrammar: async () =>
    parseRawGrammar(
      await readFile(
        new URL(
          "../../node_modules/@codingame/monaco-vscode-typescript-basics-default-extension/resources/TypeScript.tmLanguage.json",
          import.meta.url,
        ),
        "utf8",
      ),
      "TypeScript.tmLanguage.json",
    ),
});

it("updates downstream identifier scopes after an earlier multiline comment changes", async () => {
  const grammar = await registry.loadGrammar("source.ts");
  expect(grammar).not.toBeNull();
  const model = new ScopedModel(
    URI.parse("inmemory:test"),
    ["/*", "const hiddenTypoo = 1;", "*/", "const visibleTypoo = 1;"],
    "\n",
    1,
  );
  const ranges = [
    { line: 2, offset: 0, text: "const hiddenTypoo = 1;", identifier: false },
    { line: 4, offset: 0, text: "const visibleTypoo = 1;", identifier: false },
  ];
  const identifiers = model.identifiers(grammar!, ranges);
  expect(identifiers.map((token) => token.line)).toEqual([4]);
  model.onEvents({
    changes: [
      {
        range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 3 },
        rangeOffset: 0,
        rangeLength: 2,
        text: "",
      },
    ],
    eol: "\n",
    versionId: 2,
    isUndoing: false,
    isRedoing: false,
  });
  expect(model.identifiers(grammar!, ranges)).toContainEqual({
    line: 2,
    startIndex: 6,
    endIndex: 17,
  });
});

it("returns only visible identifier scopes from a long line", async () => {
  const grammar = await registry.loadGrammar("source.ts");
  const model = new ScopedModel(
    URI.parse("inmemory:test"),
    [`const visibleTypoo = 1; ${"const hiddenTypoo = 1; ".repeat(200)}`],
    "\n",
    1,
  );
  expect(
    model.identifiers(grammar!, [
      { line: 1, offset: 0, text: "const visibleTypoo = 1;", identifier: false },
    ]),
  ).toEqual([{ line: 1, startIndex: 6, endIndex: 18 }]);
});
