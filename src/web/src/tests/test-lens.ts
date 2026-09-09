// The run-lens surface: a Monaco CodeLens provider that renders "▷ Run" on each matched test block (plus a
// "▷ Run file" lens) for files a test rule matches, and the weavie.tests.runAtCursor handler. Lens clicks and
// the cursor command both dispatch weavie.tests.run {file, name?} to the Core executor. CodeLens has no
// tooltip, so each lens title carries its command's keybinding (read live from the catalog via formatKey).

import * as monaco from "monaco-editor";
import { formatKey } from "../commands/keybindings";
import { findCommand } from "../commands/registry";
import { CommandIds, type CommandResult } from "../commands/types";
import type { TextEditorConnection } from "../editor/editor-context";
import { SESSION_FILE_SCHEME, sessionForUri, sessionUriHostPath } from "../editor/session-uri";
import { onLanguageClientStarted } from "../lsp/lsp-client";
import { notify } from "../notify/notify";
import { globMatches } from "./glob";
import { testRunTargetAt } from "./test-match";
import { onTestProfileChanged, type TestRule, testRulesFor } from "./test-profile";
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
    void runOwnedTest(monaco.Uri.parse(arg.uri), arg);
  });

  const provider = monaco.languages.registerCodeLensProvider(
    { scheme: SESSION_FILE_SCHEME },
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
        const file = sessionUriHostPath(model.uri);
        const hits = await documentTestHits(model, rule);
        const lenses: monaco.languages.CodeLens[] = [];
        if (hits.length > 0 && rule.runFile !== undefined) {
          lenses.push({
            range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 },
            command: {
              id: LENS_COMMAND,
              title: `▷ Run file${shortcut(CommandIds.runTestsInFile)}`,
              arguments: [{ uri: model.uri.toString(), file }],
            },
          });
        }
        for (const hit of hits) {
          lenses.push({
            range: hit.range,
            command: {
              id: LENS_COMMAND,
              title: `▷ Run${shortcut(CommandIds.runTestAtCursor)}`,
              arguments: [{ uri: model.uri.toString(), file, name: hit.name }],
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

/** weavie.tests.runAtCursor: run the innermost test, or the file when its rule supports exact-file runs. */
export async function runTestAtCursor(
  connection: TextEditorConnection,
  selection: monaco.ISelection,
): Promise<boolean> {
  connection.signal.throwIfAborted();
  const { model } = connection;
  const position = { lineNumber: selection.positionLineNumber, column: selection.positionColumn };
  const rule = ruleForModel(model);
  if (rule === undefined) {
    return false;
  }
  const hits = await documentTestHits(model, rule);
  const target = testRunTargetAt(hits, position, rule.runFile);
  if (target === undefined) {
    notify(
      "warn",
      "This test runner cannot target every test in this file. Place the cursor inside an individual test.",
    );
    return true;
  }
  await runOwnedTest(model.uri, {
    file: sessionUriHostPath(model.uri),
    ...(target === "file" ? {} : { name: target.name }),
  });
  return true;
}

async function runOwnedTest(uri: monaco.Uri, args: { file: string; name?: string }): Promise<void> {
  const session = sessionForUri(uri);
  if (session === undefined) {
    notify("warn", "The test's owning session is no longer available.");
    return;
  }
  try {
    const result = await session
      .feature("commands")
      .request<CommandResult, { id: string; args: { file: string; name?: string } }>("invoke", {
        id: CommandIds.runTests,
        args,
      });
    if (!result.ok && result.error != null) notify("warn", result.error);
  } catch (error) {
    notify("warn", `Could not run test: ${String(error)}`);
  }
}

function ruleForModel(model: monaco.editor.ITextModel): TestRule | undefined {
  const relative = relativePath(model.uri);
  if (relative === undefined) {
    return undefined;
  }
  const session = sessionForUri(model.uri);
  return session === undefined
    ? undefined
    : testRulesFor(session.connection).find((rule) => globMatches(rule.glob, relative));
}

// The model's path relative to the workspace root (forward-slashed), or undefined when the file is outside the
// workspace — so lenses never render on files the profile's workspace-relative globs aren't meant to match.
function relativePath(uri: monaco.Uri): string | undefined {
  const root = sessionForUri(uri)?.state.lsp.current?.workspace;
  if (root === undefined) {
    return undefined;
  }
  const normalizedRoot = root.replace(/\\/g, "/").replace(/\/+$/, "");
  const normalizedPath = sessionUriHostPath(uri).replace(/\\/g, "/");
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
