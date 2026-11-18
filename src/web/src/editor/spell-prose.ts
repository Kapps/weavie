import { StandardTokenType } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/encodedTokenAttributes";
import type { ITokenizationTextModelPart } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/tokenizationTextModelPart";
import type { monaco } from "./monaco-setup";
import type { IdentifierRange } from "./spell-tokens";

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

// A longer line is minified code or data and is not checked, so a checked line is always sent whole: clipping
// one to the columns on screen would mean hit-testing it, which forces a layout on every edit and scroll.
const LINE_LIMIT = 1000;

export function visibleSpellRanges(
  editor: monaco.editor.IStandaloneCodeEditor,
  model: TokenizedModel,
): SpellSpan[] {
  const spans: SpellSpan[] = [];
  for (const range of editor.getVisibleRanges()) {
    for (let line = range.startLineNumber; line <= range.endLineNumber; line++) {
      const text = model.getLineContent(line);
      if (text.length > 0 && text.length <= LINE_LIMIT) {
        spans.push({ line, offset: 0, text, identifier: false });
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
