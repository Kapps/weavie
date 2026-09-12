import { registerSessionFeature } from "../bridge";
import { formatKey } from "../commands/keybindings";
import { findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { notify } from "../notify/notify";
import { monaco } from "./monaco-setup";
import { SESSION_FILE_SCHEME, sessionForUri } from "./session-uri-owner";
import { createSpellingActions, type SpellingActions } from "./spell-actions";
import {
  type Misspelling,
  type SpellSpan,
  spellingSpans,
  type TokenizedModel,
  visibleSpellRanges,
} from "./spell-prose";
import { createSpellingTokens } from "./spell-tokens";
import "./spell-check.css";

export interface SpellCheck extends SpellingActions {
  dispose(): void;
}

export function createSpellCheck(editor: monaco.editor.IStandaloneCodeEditor): SpellCheck {
  const decorations = editor.createDecorationsCollection();
  let words: Misspelling[] = [];
  let timer: ReturnType<typeof setTimeout> | undefined;
  let request: AbortController | undefined;
  let disposed = false;
  let validity = new AbortController();

  const check = async (): Promise<void> => {
    const model = editor.getModel() as TokenizedModel | null;
    if (
      model === null ||
      model.uri.scheme !== SESSION_FILE_SCHEME ||
      !currentEditorOptions().spellCheck
    ) {
      return;
    }
    const session = sessionForUri(model.uri);
    if (session === undefined) {
      return;
    }
    const pending = new AbortController();
    request = pending;
    const version = model.getVersionId();
    try {
      const ranges = visibleSpellRanges(editor, model);
      const tokens = await tokensSource.read(model, ranges, pending.signal);
      if (
        pending.signal.aborted ||
        model.isDisposed() ||
        editor.getModel() !== model ||
        model.getVersionId() !== version
      )
        return;
      const spans = spellingSpans(model, ranges, tokens);
      if (spans.length === 0) return;
      const result = await session
        .feature("spelling")
        .request<Misspelling[], { spans: SpellSpan[] }>("check", { spans }, pending.signal);
      if (
        pending.signal.aborted ||
        model.isDisposed() ||
        editor.getModel() !== model ||
        model.getVersionId() !== version
      ) {
        return;
      }
      words = result;
      const keys = findCommand(CommandIds.spellCorrect)?.keys.map(formatKey).join(" / ");
      const hint = `Unrecognized word. Right-click for corrections or to add to a dictionary.${keys ? ` Correct Spelling (${keys}).` : ""}`;
      decorations.set(
        result.map(({ line, offset, word }) => ({
          range: new monaco.Range(line, offset + 1, line, offset + word.length + 1),
          options: {
            description: "spelling",
            inlineClassName: "weavie-misspelling",
            stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
            hoverMessage: { value: hint },
          },
        })),
      );
    } catch (error) {
      if (!pending.signal.aborted) {
        notify("error", `Spell check failed: ${String(error)}`, "spell-check");
      }
    }
  };

  const schedule = (): void => {
    if (disposed) {
      return;
    }
    clearTimeout(timer);
    request?.abort();
    words = [];
    decorations.clear();
    timer = setTimeout(() => void check(), 250);
  };
  const tokensSource = createSpellingTokens(schedule);
  const invalidate = (): void => {
    validity.abort();
    validity = new AbortController();
    schedule();
  };
  const at = (position: monaco.IPosition | null): Misspelling | undefined =>
    position === null
      ? undefined
      : words.find(
          ({ line, offset, word }) =>
            position.lineNumber === line &&
            position.column > offset &&
            position.column <= offset + word.length + 1,
        );
  const subscriptions = [
    editor.onDidChangeModel(invalidate),
    editor.onDidChangeModelContent(invalidate),
    editor.onDidChangeModelLanguage(invalidate),
    (
      editor as monaco.editor.IStandaloneCodeEditor & {
        onDidChangeModelTokens(listener: () => void): monaco.IDisposable;
      }
    ).onDidChangeModelTokens(schedule),
    editor.onDidScrollChange(schedule),
    editor.onDidLayoutChange(schedule),
  ];
  const offOptions = onEditorOptionsChanged(invalidate);
  const offSessions = registerSessionFeature((session) =>
    session.feature("spelling").on("changed", () => {
      const model = editor.getModel();
      if (model !== null && sessionForUri(model.uri) === session) {
        invalidate();
      }
    }),
  );
  schedule();

  return {
    ...createSpellingActions(editor, at, () => validity.signal),
    dispose: () => {
      disposed = true;
      validity.abort();
      clearTimeout(timer);
      request?.abort();
      for (const subscription of subscriptions) {
        subscription.dispose();
      }
      tokensSource.dispose();
      offOptions();
      offSessions();
      decorations.clear();
    },
  };
}
