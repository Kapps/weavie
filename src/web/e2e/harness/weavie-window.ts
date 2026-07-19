// The live editor / monaco handles the app publishes on window for e2e — structural slices of the real
// IStandaloneCodeEditor and monaco namespace, declared once here because e2e sits outside the app tsconfig
// (which owns the authoritative global.d.ts declaration).

/** The slice of a Monaco text model the specs read. */
export interface ModelHandle {
  uri: unknown;
  getLineContent(line: number): string;
}

/** The slice of the live Monaco editor (window.__WEAVIE_EDITOR__) the specs drive. */
export interface EditorHandle {
  focus(): void;
  setPosition(position: { lineNumber: number; column: number }): void;
  getPosition(): { lineNumber: number; column: number } | null;
  setSelection(range: {
    startLineNumber: number;
    startColumn: number;
    endLineNumber: number;
    endColumn: number;
  }): void;
  setSelections(
    selections: {
      selectionStartLineNumber: number;
      selectionStartColumn: number;
      positionLineNumber: number;
      positionColumn: number;
    }[],
  ): void;
  getSelections(): unknown[] | null;
  getModel(): ModelHandle | null;
  getScrolledVisiblePosition(position: { lineNumber: number; column: number }): {
    top: number;
    left: number;
    height: number;
  } | null;
  getDomNode(): { getBoundingClientRect(): { left: number; top: number } } | null;
}

/** The slice of the monaco namespace (window.__WEAVIE_MONACO__) the specs use to mock LSP providers. */
export interface MonacoHandle {
  languages: {
    registerDefinitionProvider(
      selector: string,
      provider: {
        provideDefinition(model: ModelHandle): {
          uri: unknown;
          range: {
            startLineNumber: number;
            startColumn: number;
            endLineNumber: number;
            endColumn: number;
          };
        }[];
      },
    ): unknown;
  };
}

export type WeavieWindow = Window & {
  __WEAVIE_EDITOR__?: EditorHandle;
  __WEAVIE_MONACO__?: MonacoHandle;
};
