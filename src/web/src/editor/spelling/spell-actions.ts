import { monaco } from "../monaco-setup";
import type { SpellModel } from "./spell-model";
import { directSpellWord } from "./spell-protocol";
import type { SpellContext, SpellMenuTarget } from "./spell-types";

/** Resolves an active spelling issue at the editor's right-click position. */
export function contextAtClientPoint(
  editor: monaco.editor.IStandaloneCodeEditor,
  state: SpellModel | null,
  enabled: boolean,
  clientX: number,
  clientY: number,
): SpellContext | null {
  if (!enabled || state === null) {
    return null;
  }
  return state.contextAt(editor.getTargetAtClientPoint(clientX, clientY)?.position ?? null);
}

/** Resolves the cursor issue plus a client-space anchor for the keyboard spelling menu. */
export function contextAtCursor(
  editor: monaco.editor.IStandaloneCodeEditor,
  state: SpellModel | null,
  enabled: boolean,
): SpellMenuTarget | null {
  const position = editor.getPosition();
  const context = enabled && state !== null ? state.contextAt(position) : null;
  const visible = position === null ? null : editor.getScrolledVisiblePosition(position);
  const rect = editor.getDomNode()?.getBoundingClientRect();
  return context === null || visible === null
    ? null
    : {
        context,
        x: (rect?.left ?? 0) + visible.left + 8,
        y: (rect?.top ?? 0) + visible.top + visible.height,
      };
}

/** Applies a correction only when the menu/palette argument still names the live issue exactly. */
export function applySuggestion(
  editor: monaco.editor.IStandaloneCodeEditor,
  state: SpellModel | null,
  args: unknown,
): boolean {
  const context = contextFromArgs(args);
  const replacement = stringArg(args, "replacement");
  if (
    context === null ||
    replacement === null ||
    state === null ||
    !state.isCurrentContext(context)
  ) {
    return false;
  }
  const line = state.anchorLine(context.anchorId);
  return (
    line !== null &&
    editor.executeEdits("weavie-spelling", [
      {
        range: new monaco.Range(line, context.startColumn, line, context.endColumn),
        text: replacement,
      },
    ])
  );
}

/** Uses an explicit current issue when available, otherwise accepts a command's direct `word` argument. */
export function wordForDictionary(
  editor: monaco.editor.IStandaloneCodeEditor,
  state: SpellModel | null,
  args: unknown,
): string | null {
  const context = contextFromArgs(args);
  if (context !== null) {
    return state?.isCurrentContext(context) === true ? context.word : null;
  }
  const explicitWord = directSpellWord(args);
  return explicitWord ?? state?.contextAt(editor.getPosition())?.word ?? null;
}

function contextFromArgs(args: unknown): SpellContext | null {
  if (typeof args !== "object" || args === null) {
    return null;
  }
  const value = args as Record<string, unknown>;
  return typeof value.anchorId === "string" &&
    typeof value.word === "string" &&
    typeof value.startColumn === "number" &&
    typeof value.endColumn === "number" &&
    typeof value.modelEpoch === "string"
    ? {
        anchorId: value.anchorId,
        word: value.word,
        startColumn: value.startColumn,
        endColumn: value.endColumn,
        modelEpoch: value.modelEpoch,
      }
    : null;
}

function stringArg(args: unknown, name: string): string | null {
  if (typeof args !== "object" || args === null) {
    return null;
  }
  const value = (args as Record<string, unknown>)[name];
  return typeof value === "string" && value.length > 0 ? value : null;
}
