import { StandardTokenType } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/encodedTokenAttributes";
import type { ITokenizationTextModelPart } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/tokenizationTextModelPart";
import { monaco } from "./monaco-setup";
import type { IdentifierRange } from "./spell-worker";

export interface TokenizedModel extends monaco.editor.ITextModel {
  tokenization: ITokenizationTextModelPart;
}

export interface SpellSpan {
  line: number;
  offset: number;
  text: string;
  identifier: boolean;
}

export interface Misspelling {
  line: number;
  offset: number;
  word: string;
}

export function visibleSpellRanges(
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
      if (start < end) {
        spans.push({
          line,
          offset: start,
          text: text.slice(start - contextStart, end - contextStart),
          identifier: false,
        });
      }
    }
  }
  return spans;
}

export function spellingSpans(
  model: TokenizedModel,
  ranges: SpellSpan[],
  identifiers: IdentifierRange[],
): SpellSpan[] {
  const spans: SpellSpan[] = [];
  for (const range of ranges) {
    const { line, offset: start, text } = range;
    const end = start + text.length;
    const append = (from: number, to: number, identifier: boolean): void => {
      from = Math.max(start, from);
      to = Math.min(end, to);
      if (from >= to) return;
      const previous = spans.at(-1);
      if (
        !identifier &&
        !previous?.identifier &&
        previous?.line === line &&
        previous.offset + previous.text.length === from
      ) {
        previous.text += text.slice(from - start, to - start);
      } else {
        spans.push({
          line,
          offset: from,
          text: text.slice(from - start, to - start),
          identifier,
        });
      }
    };
    if (model.getLanguageId() === "plaintext") {
      append(start, end, false);
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
        append(offset, tokens.getEndOffset(token), false);
      } else if (type === StandardTokenType.Other) {
        for (const identifier of identifiers) {
          if (identifier.line !== line) continue;
          if (identifier.startIndex >= tokens.getEndOffset(token)) break;
          append(
            Math.max(offset, identifier.startIndex),
            Math.min(tokens.getEndOffset(token), identifier.endIndex),
            true,
          );
        }
      }
    }
  }
  return spans;
}
