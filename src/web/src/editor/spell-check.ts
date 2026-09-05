import { StandardTokenType } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/encodedTokenAttributes";
import type { ITokenizationTextModelPart } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/tokenizationTextModelPart";
import { registerSessionFeature } from "../bridge";
import type { ContextMenuEntry } from "../chrome/ContextMenu";
import { CommandIds } from "../commands/types";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { notify } from "../notify/notify";
import { monaco } from "./monaco-setup";
import { SESSION_FILE_SCHEME, sessionForUri } from "./session-uri-owner";
import "./spell-check.css";

interface TokenizedModel extends monaco.editor.ITextModel {
  tokenization: ITokenizationTextModelPart;
}

interface SpellSpan {
  line: number;
  offset: number;
  text: string;
}

interface Misspelling {
  line: number;
  offset: number;
  word: string;
}

export interface SpellCheck {
  menuAt(x: number, y: number): ContextMenuEntry[];
  add(scope: "user" | "project", args: unknown): Promise<void>;
  dispose(): void;
}

function visibleProse(
  editor: monaco.editor.IStandaloneCodeEditor,
  model: TokenizedModel,
): SpellSpan[] {
  const spans: SpellSpan[] = [];
  const unwrapped = editor.getOption(monaco.editor.EditorOption.wrappingInfo).wrappingColumn === -1;
  const bounds = unwrapped ? editor.getDomNode()?.getBoundingClientRect() : undefined;
  const layout = editor.getLayoutInfo();
  for (const range of editor.getVisibleRanges()) {
    for (let line = range.startLineNumber; line <= range.endLineNumber; line++) {
      const limit = model.getLineMaxColumn(line) - 1;
      let start = line === range.startLineNumber ? range.startColumn - 1 : 0;
      let end = line === range.endLineNumber ? range.endColumn - 1 : limit;
      if (unwrapped) {
        const row = editor.getScrolledVisiblePosition({ lineNumber: line, column: 1 });
        if (bounds === undefined || row === null) continue;
        const y = bounds.top + Math.max(0, Math.min(layout.height - 1, row.top + row.height / 2));
        const left =
          row.left >= layout.contentLeft
            ? { lineNumber: line, column: 1 }
            : editor.getTargetAtClientPoint(bounds.left + layout.contentLeft + 1, y)?.position;
        const right = editor.getTargetAtClientPoint(
          bounds.left +
            layout.contentLeft +
            layout.contentWidth -
            layout.verticalScrollbarWidth -
            1,
          y,
        )?.position;
        if (left?.lineNumber !== line || right?.lineNumber !== line) continue;
        start = Math.max(start, left.column - 1);
        end = Math.min(end, right.column - 1);
      }
      const contextStart = Math.max(0, start - 1);
      const text = model.getValueInRange(
        new monaco.Range(line, contextStart + 1, line, Math.min(limit, end + 1) + 1),
      );
      // A word cut by the viewport is checked when visible in full; never scan an offscreen long token.
      if (start > 0 && !/\s/.test(text.charAt(start - contextStart - 1))) {
        while (start < end && !/\s/.test(text.charAt(start - contextStart))) start++;
      }
      if (end < limit && !/\s/.test(text.charAt(end - contextStart))) {
        while (end > start && !/\s/.test(text.charAt(end - contextStart - 1))) end--;
      }
      const append = (from: number, to: number): void => {
        from = Math.max(start, from);
        to = Math.min(end, to);
        if (from >= to) return;
        const previous = spans.at(-1);
        if (previous?.line === line && previous.offset + previous.text.length === from) {
          previous.text += text.slice(from - contextStart, to - contextStart);
        } else {
          spans.push({
            line,
            offset: from,
            text: text.slice(from - contextStart, to - contextStart),
          });
        }
      };
      if (model.getLanguageId() === "plaintext") {
        append(start, end);
        continue;
      }
      if (!model.tokenization.hasAccurateTokensForLine(line)) {
        continue;
      }
      const tokens = model.tokenization.getLineTokens(line);
      for (let token = tokens.findTokenIndexAtOffset(start); token < tokens.getCount(); token++) {
        const offset = tokens.getStartOffset(token);
        if (offset >= end) break;
        const type = tokens.getStandardTokenType(token);
        if (
          model.getLanguageId() === "markdown"
            ? tokens.getLanguageId(token) === "markdown"
            : type === StandardTokenType.Comment || type === StandardTokenType.String
        ) {
          append(offset, tokens.getEndOffset(token));
        }
      }
    }
  }
  return spans;
}

export function createSpellCheck(editor: monaco.editor.IStandaloneCodeEditor): SpellCheck {
  const decorations = editor.createDecorationsCollection();
  let words: Misspelling[] = [];
  let timer: ReturnType<typeof setTimeout> | undefined;
  let request: AbortController | undefined;
  let disposed = false;

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
    const spans = visibleProse(editor, model);
    if (spans.length === 0) return;
    const pending = new AbortController();
    request = pending;
    const version = model.getVersionId();
    try {
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
      decorations.set(
        result.map(({ line, offset, word }) => ({
          range: new monaco.Range(line, offset + 1, line, offset + word.length + 1),
          options: {
            description: "spelling",
            inlineClassName: "weavie-misspelling",
            stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
            hoverMessage: { value: "Unrecognized word. Right-click to add to a dictionary." },
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
    editor.onDidChangeModel(schedule),
    editor.onDidChangeModelContent(schedule),
    editor.onDidChangeModelLanguage(schedule),
    (
      editor as monaco.editor.IStandaloneCodeEditor & {
        onDidChangeModelTokens(listener: () => void): monaco.IDisposable;
      }
    ).onDidChangeModelTokens(schedule),
    editor.onDidScrollChange(schedule),
    editor.onDidLayoutChange(schedule),
  ];
  const offOptions = onEditorOptionsChanged(schedule);
  const offSessions = registerSessionFeature((session) =>
    session.feature("spelling").on("changed", () => {
      const model = editor.getModel();
      if (model !== null && sessionForUri(model.uri) === session) {
        schedule();
      }
    }),
  );
  schedule();

  return {
    menuAt: (x, y) => {
      const hit = at(editor.getTargetAtClientPoint(x, y)?.position ?? null);
      const model = editor.getModel();
      if (hit === undefined || model === null) {
        return [];
      }
      const args = { word: hit.word, uri: model.uri.toString() };
      return [
        {
          kind: "submenu",
          label: `Add “${hit.word}” to Dictionary`,
          entries: [
            { commandId: CommandIds.spellAddUser, label: "User Dictionary", args },
            { commandId: CommandIds.spellAddProject, label: "Project Dictionary", args },
          ],
        },
        { kind: "separator" },
      ];
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
    dispose: () => {
      disposed = true;
      clearTimeout(timer);
      request?.abort();
      for (const subscription of subscriptions) {
        subscription.dispose();
      }
      offOptions();
      offSessions();
      decorations.clear();
    },
  };
}
