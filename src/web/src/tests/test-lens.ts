// The run-lens surface: a Monaco CodeLens provider that renders "▷ Run" on each matched test block (plus a
// "▷ Run file" lens) for files a test rule matches, and the weavie.tests.runAtCursor handler. Lens clicks and
// the cursor command both dispatch weavie.tests.run {file, name?} to the Core executor. CodeLens has no
// tooltip, so each lens title carries its command's keybinding (read live from the catalog via formatKey).

import * as monaco from "monaco-editor";
import { formatKey } from "../commands/keybindings";
import { dispatchCommand, findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { currentWorkspaceRoot, onLanguageClientStarted } from "../lsp/lsp-client";
import { globMatches } from "./glob";
import { type TestRule, onTestProfileChanged, testRules } from "./test-profile";
import { documentTestHits } from "./test-symbols";

// An internal monaco command the lens click invokes; it forwards to the Core weavie.tests.run command.
const LENS_COMMAND = "weavie.tests._runLens";

let installed = false;

/** Registers the run-lens provider and the lens-click command once (idempotent across hot reloads). */
export function installTestLenses(): void {
  if (installed) {
    return;
  }
  installed = true;
  const emitter = new monaco.Emitter<void>();

  const lensCommand = monaco.editor.registerCommand(LENS_COMMAND, (_accessor, arg) => {
    void dispatchCommand(CommandIds.runTests, arg);
  });

  const provider = monaco.languages.registerCodeLensProvider(
    { scheme: "file" },
    {
      // monaco types onDidChange as IEvent<this>; the payload is unused (it only signals "refresh").
      onDidChange: emitter.event as unknown as monaco.IEvent<monaco.languages.CodeLensProvider>,
      provideCodeLenses: async (model) => {
        const rule = ruleForModel(model);
        if (rule === undefined) {
          return { lenses: [], dispose() {} };
        }
        const file = model.uri.fsPath;
        const hits = await documentTestHits(model, rule);
        const lenses: monaco.languages.CodeLens[] = [];
        if (hits.length > 0) {
          lenses.push({
            range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 },
            command: {
              id: LENS_COMMAND,
              title: `▷ Run file${shortcut(CommandIds.runTestsInFile)}`,
              arguments: [{ file }],
            },
          });
        }
        for (const hit of hits) {
          lenses.push({
            range: hit.range,
            command: {
              id: LENS_COMMAND,
              title: `▷ Run${shortcut(CommandIds.runTestAtCursor)}`,
              arguments: [{ file, name: hit.name }],
            },
          });
        }
        return { lenses, dispose() {} };
      },
    },
  );

  // Refresh lenses when the profile changes or a language client (re)starts — the first symbol query can
  // precede server readiness, so an early empty result must be re-run once the server can answer.
  onTestProfileChanged(() => emitter.fire());
  onLanguageClientStarted(() => emitter.fire());
  // Never torn down: the lens surface lives for the page's lifetime (guarded above against a double install).
  void provider;
  void lensCommand;
}

/** weavie.tests.runAtCursor: run the innermost test block containing the cursor (or the file if none). */
export async function runTestAtCursor(): Promise<boolean> {
  const editor = monaco.editor.getEditors().find((e) => e.hasTextFocus());
  const model = editor?.getModel();
  const position = editor?.getPosition();
  if (model == null || position == null) {
    return false;
  }
  const rule = ruleForModel(model);
  if (rule === undefined) {
    return false;
  }
  const hits = await documentTestHits(model, rule);
  const innermost = hits
    .filter((h) => rangeContains(h.fullRange, position))
    .sort((a, b) => rangeArea(a.fullRange) - rangeArea(b.fullRange))[0];
  if (innermost === undefined) {
    void dispatchCommand(CommandIds.runTests, { file: model.uri.fsPath });
    return true;
  }
  void dispatchCommand(CommandIds.runTests, { file: model.uri.fsPath, name: innermost.name });
  return true;
}

function ruleForModel(model: monaco.editor.ITextModel): TestRule | undefined {
  const relative = relativePath(model.uri);
  if (relative === undefined) {
    return undefined;
  }
  return testRules().find((rule) => globMatches(rule.glob, relative));
}

// The model's path relative to the workspace root (forward-slashed). Falls back to the full path when the file
// is outside the workspace — such a path won't match the typical **-rooted rule, so no lenses render.
function relativePath(uri: monaco.Uri): string | undefined {
  const root = currentWorkspaceRoot();
  if (root === undefined) {
    return undefined;
  }
  const normalizedRoot = root.replace(/\\/g, "/").replace(/\/+$/, "");
  const normalizedPath = uri.fsPath.replace(/\\/g, "/");
  if (normalizedPath === normalizedRoot) {
    return "";
  }
  if (normalizedPath.startsWith(`${normalizedRoot}/`)) {
    return normalizedPath.slice(normalizedRoot.length + 1);
  }
  return normalizedPath;
}

function shortcut(commandId: string): string {
  const key = findCommand(commandId)?.keys[0];
  return key !== undefined ? ` (${formatKey(key)})` : "";
}

function rangeContains(
  range: { startLineNumber: number; startColumn: number; endLineNumber: number; endColumn: number },
  position: monaco.Position,
): boolean {
  if (position.lineNumber < range.startLineNumber || position.lineNumber > range.endLineNumber) {
    return false;
  }
  if (position.lineNumber === range.startLineNumber && position.column < range.startColumn) {
    return false;
  }
  if (position.lineNumber === range.endLineNumber && position.column > range.endColumn) {
    return false;
  }
  return true;
}

function rangeArea(range: { startLineNumber: number; endLineNumber: number }): number {
  return range.endLineNumber - range.startLineNumber;
}
