// The run-lens surface: a Monaco CodeLens provider that renders "▷ Run" on each matched test block (plus a
// "▷ Run file" lens) for files a test rule matches, and the weavie.tests.runAtCursor handler. Lens clicks and
// the cursor command both dispatch weavie.tests.run {file, name?} to the Core executor. CodeLens has no
// tooltip, so each lens title carries its command's keybinding (read live from the catalog via formatKey).

import * as monaco from "monaco-editor";
import { formatKey } from "../commands/keybindings";
import { dispatchCommand, findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { uriHostPath } from "../editor/fs-path";
import { activeCodeEditor } from "../editor/vscode-services";
import { currentWorkspaceRoot, onLanguageClientStarted } from "../lsp/lsp-client";
import { globMatches } from "./glob";
import { onTestProfileChanged, type TestRule, testRules } from "./test-profile";
import { documentTestHits, innermostHitAt } from "./test-symbols";

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
        // uriHostPath, never fsPath: this path is dispatched to Core to compose a shell command, so it must be
        // host-native — a Windows client's fsPath backslashes a POSIX host's path and no test rule would match.
        const file = uriHostPath(model.uri);
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
  // Prefer the focused editor, but fall back to the active one so this runs from the palette too (there the
  // omnibar input holds focus, not the editor) — acting on the editor's last cursor position.
  const editor = monaco.editor.getEditors().find((e) => e.hasTextFocus()) ?? activeCodeEditor();
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
  const innermost = innermostHitAt(hits, position);
  if (innermost === undefined) {
    void dispatchCommand(CommandIds.runTests, { file: uriHostPath(model.uri) });
    return true;
  }
  void dispatchCommand(CommandIds.runTests, { file: uriHostPath(model.uri), name: innermost.name });
  return true;
}

function ruleForModel(model: monaco.editor.ITextModel): TestRule | undefined {
  const relative = relativePath(model.uri);
  if (relative === undefined) {
    return undefined;
  }
  return testRules().find((rule) => globMatches(rule.glob, relative));
}

// The model's path relative to the workspace root (forward-slashed), or undefined when the file is outside the
// workspace — so lenses never render on files the profile's workspace-relative globs aren't meant to match.
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
  return normalizedPath.startsWith(`${normalizedRoot}/`)
    ? normalizedPath.slice(normalizedRoot.length + 1)
    : undefined;
}

function shortcut(commandId: string): string {
  const key = findCommand(commandId)?.keys[0];
  return key !== undefined ? ` (${formatKey(key)})` : "";
}
