import type * as monaco from "monaco-editor";

/** Monaco's hidden-area API: on the editor widget, not on its public type. */
export interface HiddenAreasEditor {
  setHiddenAreas(ranges: monaco.IRange[], source: unknown): void;
  onDidChangeHiddenAreas: monaco.IEvent<void>;
}

/** The hidden-area API of `editor`. */
export const hiddenAreas = (editor: monaco.editor.ICodeEditor): HiddenAreasEditor =>
  editor as unknown as HiddenAreasEditor;
