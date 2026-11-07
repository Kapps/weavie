import type { TextEditorConnection } from "../editor-context";

/** The tab's own working copy as Preview sees it; every operation targets that exact model. */
export interface PreviewDocument {
  /** The working copy's text, or undefined while it isn't loaded or can't be edited. */
  editableText(): string | undefined;
  /** Replaces 0-based lines `[start, end)`. */
  replaceLines(start: number, end: number, lines: string[]): void;
  /** Closes the current undo step. */
  checkpoint(): void;
  undo(): void;
  redo(): void;
  /** The working copy's model URI, while it's loaded. */
  uri(): string | undefined;
  /** The 1-based line of the cursor in the editor under the preview: the review position. */
  cursorLine(): number;
  setCursor(line: number): void;
}

export function createPreviewDocument(
  connection: () => TextEditorConnection | undefined,
): PreviewDocument {
  const model = () => {
    const live = connection();
    if (live === undefined) throw new Error("The previewed file is no longer open.");
    return live.model;
  };
  const trigger = (action: "undo" | "redo"): void => {
    connection()?.editor.trigger("preview", action, null);
  };
  return {
    editableText() {
      const live = connection();
      return live === undefined || live.editor.getRawOptions().readOnly === true
        ? undefined
        : live.model.getValue();
    },
    replaceLines(start, end, lines) {
      if (!Number.isInteger(start) || !Number.isInteger(end))
        throw new Error(`Invalid line range ${start}-${end}.`);
      const text = model();
      const count = text.getLineCount();
      const body = lines.join("\n");
      const edit =
        end < count
          ? {
              range: {
                startLineNumber: start + 1,
                startColumn: 1,
                endLineNumber: end + 1,
                endColumn: 1,
              },
              text: lines.length === 0 ? "" : `${body}\n`,
            }
          : start >= count
            ? { range: endOf(count, text.getLineMaxColumn(count)), text: `\n${body}` }
            : lines.length === 0 && start > 0
              ? {
                  range: {
                    ...endOf(start, text.getLineMaxColumn(start)),
                    endLineNumber: count,
                    endColumn: text.getLineMaxColumn(count),
                  },
                  text: "",
                }
              : {
                  range: {
                    startLineNumber: start + 1,
                    startColumn: 1,
                    endLineNumber: count,
                    endColumn: text.getLineMaxColumn(count),
                  },
                  text: body,
                };
      text.pushEditOperations([], [edit], () => null);
    },
    checkpoint() {
      connection()?.model.pushStackElement();
    },
    undo: () => trigger("undo"),
    redo: () => trigger("redo"),
    uri: () => connection()?.model.uri.toString(),
    cursorLine: () => connection()?.editor.getPosition()?.lineNumber ?? 1,
    setCursor: (line) => connection()?.editor.setPosition({ lineNumber: line, column: 1 }),
  };
}

const endOf = (line: number, column: number) => ({
  startLineNumber: line,
  startColumn: column,
  endLineNumber: line,
  endColumn: column,
});
