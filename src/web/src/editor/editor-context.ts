import type * as monaco from "monaco-editor";
import type { ClientSession } from "../bridge";
import type { SymbolQuerySource } from "../symbols/symbol-match";
import type { GitBlameController } from "./git-blame";
import type { NavLocation } from "./nav-history";
import type { SpellCheck } from "./spell-check";

export interface TextEditorConnection {
  readonly session: ClientSession;
  readonly kind: "file" | "review";
  readonly editor: monaco.editor.IStandaloneCodeEditor;
  readonly model: monaco.editor.ITextModel;
  readonly signal: AbortSignal;
  readonly symbols: SymbolQuerySource;
  readonly blame: GitBlameController;
  readonly spelling: SpellCheck;
  capture(): NavLocation | undefined;
  restore(location: NavLocation): void;
}

/** Registrations belong to exact sessions and model bindings, never to the shared widget's next model. */
export function createEditorContexts() {
  const owners = new WeakMap<
    ClientSession,
    {
      surface: () => "file" | "review";
      current: Map<"file" | "review", TextEditorConnection>;
    }
  >();
  const widgets = new WeakMap<monaco.editor.ICodeEditor, TextEditorConnection>();
  const listeners = new Set<(connection: TextEditorConnection) => void>();
  const owner = (session: ClientSession) => {
    const state = owners.get(session);
    if (state === undefined) throw new Error("This session has no editor owner.");
    return state;
  };
  const live = (connection: TextEditorConnection): boolean =>
    !connection.signal.aborted &&
    !connection.session.signal.aborted &&
    connection.editor.getModel() === connection.model;
  const activate = (connection: TextEditorConnection): void => {
    if (!live(connection)) return;
    owner(connection.session).current.set(connection.kind, connection);
  };
  return {
    live,
    activate,
    isCurrent(connection: TextEditorConnection): boolean {
      const state = owners.get(connection.session);
      return state?.current.get(state.surface()) === connection;
    },
    own(session: ClientSession, surface: () => "file" | "review"): () => void {
      const state = { surface, current: new Map<"file" | "review", TextEditorConnection>() };
      owners.set(session, state);
      return () => {
        if (owners.get(session) === state) owners.delete(session);
      };
    },
    get(session: ClientSession): TextEditorConnection | undefined {
      const state = owners.get(session);
      const connection = state?.current.get(state.surface());
      return connection !== undefined && live(connection) ? connection : undefined;
    },
    fromEditor(editor: monaco.editor.ICodeEditor): TextEditorConnection | undefined {
      const connection = widgets.get(editor);
      return connection !== undefined && live(connection) ? connection : undefined;
    },
    register(connection: TextEditorConnection): () => void {
      widgets.set(connection.editor, connection);
      if (!owner(connection.session).current.has(connection.kind)) activate(connection);
      return () => {
        if (widgets.get(connection.editor) === connection) widgets.delete(connection.editor);
        const state = owners.get(connection.session);
        if (state?.current.get(connection.kind) === connection)
          state.current.delete(connection.kind);
      };
    },
    changed(connection: TextEditorConnection): void {
      if (live(connection) && this.get(connection.session) === connection) {
        for (const listener of listeners) listener(connection);
      }
    },
    onChange(listener: (connection: TextEditorConnection) => void): () => void {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

export const editorContexts = createEditorContexts();
