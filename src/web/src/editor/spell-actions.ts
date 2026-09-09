import type { ContextMenuContent } from "../chrome/ContextMenu";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { monaco } from "./monaco-setup";
import { sessionForUri } from "./session-uri-owner";
import type { Misspelling } from "./spell-prose";

export interface SpellingActions {
  menuAt(x: number, y: number): ContextMenuContent;
  correct(args: unknown): ContextMenuContent | null;
  add(scope: "user" | "project", args: unknown): Promise<void>;
}

export function createSpellingActions(
  editor: monaco.editor.IStandaloneCodeEditor,
  at: (position: monaco.IPosition | null) => Misspelling | undefined,
  validity: () => AbortSignal,
): SpellingActions {
  let serial = 0;
  let correction:
    | { id: number; valid: () => boolean; range: monaco.Range; suggestions: string[] }
    | undefined;

  const menu = (hit: Misspelling | undefined, x: number, y: number): ContextMenuContent => {
    const model = editor.getModel();
    const session = model === null ? undefined : sessionForUri(model.uri);
    if (hit === undefined || model === null || session === undefined) return { x, y, entries: [] };
    const args = { word: hit.word, uri: model.uri.toString() };
    const version = model.getVersionId();
    const signal = validity();
    const range = new monaco.Range(
      hit.line,
      hit.offset + 1,
      hit.line,
      hit.offset + hit.word.length + 1,
    );
    const valid = (): boolean =>
      !editor.getOption(monaco.editor.EditorOption.readOnly) &&
      !signal.aborted &&
      !model.isDisposed() &&
      editor.getModel() === model &&
      model.getVersionId() === version &&
      model.getValueInRange(range) === hit.word;
    const target = { id: ++serial, valid, range, suggestions: [] as string[] };
    correction = target;
    return {
      x,
      y,
      entries: [
        {
          kind: "submenu",
          label: `Add “${hit.word}” to Dictionary`,
          entries: [
            { commandId: CommandIds.spellAddUser, label: "User Dictionary", args },
            { commandId: CommandIds.spellAddProject, label: "Project Dictionary", args },
          ],
        },
        { kind: "separator" },
      ],
      loadEntries: async (menuSignal) => {
        const combined = AbortSignal.any([signal, menuSignal]);
        let suggestions: string[];
        try {
          suggestions = await session
            .feature("spelling")
            .request<string[], { word: string }>("suggest", { word: hit.word }, combined);
        } catch (error) {
          if (combined.aborted) return [];
          throw error;
        }
        if (combined.aborted || !valid()) return [];
        target.suggestions = suggestions;
        return suggestions.length === 0
          ? [
              {
                commandId: CommandIds.spellCorrect,
                label: "No spelling suggestions",
                disabled: true,
              },
            ]
          : suggestions.map((replacement) => ({
              commandId: CommandIds.spellCorrect,
              label: replacement,
              args: { replacement, id: target.id },
              hideKeys: true,
            }));
      },
    };
  };

  return {
    menuAt: (x, y) => menu(at(editor.getTargetAtClientPoint(x, y)?.position ?? null), x, y),
    correct: (args) => {
      const replacement = (args as { replacement?: unknown } | null)?.replacement;
      if (typeof replacement === "string") {
        const target = correction;
        const expected = args as { id: number };
        if (
          target === undefined ||
          !target.valid() ||
          !target.suggestions.includes(replacement) ||
          target.id !== expected.id
        ) {
          notify("info", "The spelling target changed. Open its suggestions again.");
          return null;
        }
        editor.pushUndoStop();
        editor.executeEdits("spelling", [{ range: target.range, text: replacement }]);
        editor.pushUndoStop();
        editor.focus();
        return null;
      }
      const position = editor.getPosition();
      const hit = at(position);
      const bounds = editor.getDomNode()?.getBoundingClientRect();
      const point = position === null ? null : editor.getScrolledVisiblePosition(position);
      if (hit === undefined || bounds === undefined || point === null) {
        notify("info", "Place the cursor on an underlined word to see spelling suggestions.");
        return null;
      }
      return menu(hit, bounds.left + point.left, bounds.top + point.top + point.height);
    },
    add: async (scope, args) => {
      const target = args as { word?: unknown; uri?: unknown } | null;
      const word = typeof target?.word === "string" ? target.word : at(editor.getPosition())?.word;
      const uri =
        typeof target?.uri === "string" ? monaco.Uri.parse(target.uri) : editor.getModel()?.uri;
      const session = uri === undefined ? undefined : sessionForUri(uri);
      if (word === undefined || session === undefined) {
        notify("info", "Place the cursor on an underlined word to add it to a dictionary.");
        return;
      }
      await session
        .feature("spelling")
        .request<boolean, { scope: string; word: string }>("add", { scope, word });
      notify("info", `Added “${word}” to the ${scope} dictionary.`);
    },
  };
}
