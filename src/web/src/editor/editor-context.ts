import type * as monaco from "monaco-editor";
import type { ClientSession } from "../bridge";
import type { SymbolQuerySource } from "../symbols/symbol-match";
import type { GitBlameController } from "./git-blame";
import type { TextLocation } from "./nav-history";
import type { SpellCheck } from "./spell-check";
import type { TabOwner } from "./tab-owner";

export interface TextEditorConnection {
  readonly session: ClientSession;
  readonly tab: TabOwner;
  readonly editor: monaco.editor.IStandaloneCodeEditor;
  readonly model: monaco.editor.ITextModel;
  readonly signal: AbortSignal;
  readonly symbols: SymbolQuerySource;
  readonly blame: GitBlameController;
  readonly spelling: SpellCheck;
  capture(): TextLocation | undefined;
  restore(location: TextLocation): void;
}

export type TextEditorMenuHandler = (
  connection: TextEditorConnection,
  x: number,
  y: number,
) => void;

/** Registrations belong to exact sessions and model bindings, never to the shared widget's next model. */
export function createEditorContexts() {
  const owners = new WeakMap<
    ClientSession,
    {
      active: () => TabOwner | undefined;
      openMenu: TextEditorMenuHandler;
      current: Map<TabOwner, TextEditorConnection>;
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
    !connection.tab.signal.aborted &&
    !connection.session.signal.aborted &&
    connection.editor.getModel() === connection.model;
  const displayed = (connection: TextEditorConnection): boolean =>
    live(connection) &&
    owners.get(connection.session)?.active() === connection.tab &&
    connection.tab.presentation?.text === true;
  const activate = (connection: TextEditorConnection): void => {
    if (!live(connection)) return;
    owner(connection.session).current.set(connection.tab, connection);
  };
  return {
    live,
    displayed,
    activate,
    isCurrent(connection: TextEditorConnection): boolean {
      const state = owners.get(connection.session);
      return state?.current.get(state.active()!) === connection;
    },
    openMenu(connection: TextEditorConnection, x: number, y: number): void {
      if (displayed(connection)) owner(connection.session).openMenu(connection, x, y);
    },
    own(
      session: ClientSession,
      active: () => TabOwner | undefined,
      openMenu: TextEditorMenuHandler,
    ): () => void {
      const state = {
        active,
        openMenu,
        current: new Map<TabOwner, TextEditorConnection>(),
      };
      owners.set(session, state);
      return () => {
        if (owners.get(session) === state) owners.delete(session);
      };
    },
    forTab(tab: TabOwner): TextEditorConnection | undefined {
      const connection = owners.get(tab.session)?.current.get(tab);
      return connection !== undefined && live(connection) ? connection : undefined;
    },
    get(session: ClientSession): TextEditorConnection | undefined {
      const state = owners.get(session);
      const connection = state?.current.get(state.active()!);
      return connection !== undefined && displayed(connection) ? connection : undefined;
    },
    fromEditor(editor: monaco.editor.ICodeEditor): TextEditorConnection | undefined {
      const connection = widgets.get(editor);
      return connection !== undefined && live(connection) ? connection : undefined;
    },
    register(connection: TextEditorConnection): () => void {
      widgets.set(connection.editor, connection);
      if (!owner(connection.session).current.has(connection.tab)) activate(connection);
      return () => {
        if (widgets.get(connection.editor) === connection) widgets.delete(connection.editor);
        const state = owners.get(connection.session);
        if (state?.current.get(connection.tab) === connection) state.current.delete(connection.tab);
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
