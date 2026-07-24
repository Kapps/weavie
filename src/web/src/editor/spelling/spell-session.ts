import {
  currentEditorBinding,
  editorAttribution,
  postToEditorBinding,
  type SpellProjection,
} from "../../bridge";
import { notify } from "../../notify/notify";
import {
  currentSpellSettings,
  onSpellSettingsChanged,
  type SpellSettings,
} from "../../spell-settings";
import { normalizePath, uriHostPath } from "../fs-path";
import type { monaco } from "../monaco-setup";
import {
  applySuggestion,
  contextAtClientPoint,
  contextAtCursor,
  wordForDictionary,
} from "./spell-actions";
import { SpellModel } from "./spell-model";
import { ownsSpellProjection } from "./spell-protocol";
import type {
  SpellAddWordResult,
  SpellContext,
  SpellDiagnostics,
  SpellDictionaryChanged,
  SpellMenuTarget,
  SpellScope,
  SpellSuggestResult,
} from "./spell-types";

export type {
  SpellAddWordResult,
  SpellContext,
  SpellDiagnostics,
  SpellDictionaryChanged,
  SpellMenuTarget,
  SpellScope,
  SpellSuggestResult,
} from "./spell-types";

interface SuggestRequest {
  state: SpellModel;
  context: SpellContext;
  locale: string;
  resolve: (suggestions: string[]) => void;
  reject: (error: Error) => void;
}

interface AddRequest {
  word: string;
  scope: SpellScope;
  resolve: () => void;
  reject: (error: Error) => void;
}

const DOCUMENT_CHANGE_DELAY_MS = 150;

/** Sends open working copies to Core and renders its versioned whole-document diagnostics. */
export class SpellSession {
  private readonly states = new Map<monaco.editor.ITextModel, SpellModel>();
  private readonly underlines: monaco.editor.IEditorDecorationsCollection;
  private readonly suggestions = new Map<string, SuggestRequest>();
  private readonly additions = new Map<string, AddRequest>();
  private readonly settingsSubscription: () => void;
  private documentRevision = 0;
  private requestSequence = 0;
  private settings = currentSpellSettings();

  constructor(private readonly editor: monaco.editor.IStandaloneCodeEditor) {
    this.underlines = editor.createDecorationsCollection();
    this.settingsSubscription = onSpellSettingsChanged((settings) => this.applySettings(settings));
    editor.onDidChangeModel(() => this.render());
  }

  track(model: monaco.editor.ITextModel): void {
    if (this.states.has(model)) {
      return;
    }
    const state = new SpellModel(model);
    this.states.set(model, state);
    state.track(() => {
      this.states.delete(model);
      this.render();
    });
    this.schedule(state);
    this.render();
  }

  contentChanged(model: monaco.editor.ITextModel): void {
    const state = this.states.get(model);
    if (state === undefined) {
      return;
    }
    state.clear();
    this.render();
    this.schedule(state);
  }

  clearProjection(): void {
    for (const state of this.states.values()) {
      state.clear();
    }
    this.rejectAdditions("The editor session changed.");
    this.rejectSuggestions("The editor session changed.");
    this.render();
  }

  contextAtClientPoint(clientX: number, clientY: number): SpellContext | null {
    return contextAtClientPoint(
      this.editor,
      this.currentState(),
      this.settings.enabled,
      clientX,
      clientY,
    );
  }

  contextAtCursor(): SpellMenuTarget | null {
    return contextAtCursor(this.editor, this.currentState(), this.settings.enabled);
  }

  isCurrentContext(context: SpellContext): boolean {
    return this.settings.enabled && this.currentState()?.isCurrentContext(context) === true;
  }

  requestSuggestions(context: SpellContext): Promise<string[]> {
    const state = this.currentState();
    const binding = currentEditorBinding();
    if (
      !this.settings.enabled ||
      state === null ||
      binding === null ||
      binding.protocol !== "projection" ||
      !state.isCurrentContext(context)
    ) {
      return Promise.reject(new Error("The spelling target is no longer current."));
    }
    this.rejectSuggestions("A newer spelling suggestion request replaced this one.");
    const requestId = this.nextRequest("suggest");
    return new Promise<string[]>((resolve, reject) => {
      this.suggestions.set(requestId, {
        state,
        context,
        locale: this.settings.locale,
        resolve,
        reject,
      });
      postToEditorBinding(binding, {
        type: "spell-suggest",
        requestId,
        word: context.word,
        ...editorAttribution(binding),
      });
    });
  }

  applySuggestion(args: unknown): boolean {
    return applySuggestion(this.editor, this.currentState(), args);
  }

  addWord(scope: SpellScope, args: unknown): Promise<void> {
    const word = wordForDictionary(this.editor, this.currentState(), args);
    if (word === null) {
      return Promise.reject(new Error("No spelling word is available."));
    }
    const binding = currentEditorBinding();
    if (binding === null || binding.protocol !== "projection") {
      return Promise.reject(new Error("The spelling target is no longer current."));
    }
    const requestId = this.nextRequest("add");
    return new Promise<void>((resolve, reject) => {
      this.additions.set(requestId, { word, scope, resolve, reject });
      postToEditorBinding(binding, {
        type: "spell-add-word",
        requestId,
        word,
        scope,
        ...editorAttribution(binding),
      });
    });
  }

  handleDiagnostics(message: SpellDiagnostics): void {
    if (!this.ownsProjection(message)) {
      return;
    }
    const pathKey = normalizePath(message.path);
    if (message.error !== undefined) {
      notify("warn", `Spell check failed: ${message.error}`, "spell-check");
    }
    for (const state of this.states.values()) {
      if (this.pathKey(state) === pathKey) {
        state.applyDiagnostics(
          message.error === undefined ? message.issues : [],
          message.documentRevision,
        );
      }
    }
    this.render();
  }

  handleSuggestResult(message: SpellSuggestResult): void {
    const request = this.suggestions.get(message.requestId);
    this.suggestions.delete(message.requestId);
    if (request === undefined) {
      return;
    }
    if (
      !this.ownsProjection(message) ||
      request.locale !== message.locale ||
      message.locale !== this.settings.locale ||
      !request.state.isCurrentContext(request.context)
    ) {
      request.reject(new Error("The spelling target is no longer current."));
      return;
    }
    if (message.error !== undefined) {
      notify("warn", `Spelling suggestions failed: ${message.error}`, "spell-suggestions");
      request.reject(new Error(message.error));
      return;
    }
    request.resolve(message.suggestions);
  }

  handleAddWordResult(message: SpellAddWordResult): void {
    const request = this.additions.get(message.requestId);
    this.additions.delete(message.requestId);
    if (request === undefined) {
      return;
    }
    if (!this.ownsProjection(message)) {
      request.reject(new Error("The spelling target is no longer current."));
      return;
    }
    if (!message.ok || message.error !== undefined) {
      request.reject(new Error(message.error ?? "The word could not be added to the dictionary."));
      return;
    }
    notify("info", `Added “${request.word}” to the ${request.scope} dictionary.`);
    request.resolve();
  }

  handleDictionaryChanged(message: SpellDictionaryChanged): void {
    if (!this.ownsProjection(message)) {
      return;
    }
    this.rejectSuggestions("The dictionary changed.");
    this.refreshDocuments();
  }

  dispose(): void {
    this.settingsSubscription();
    this.rejectAdditions("The editor was disposed.");
    this.rejectSuggestions("The editor was disposed.");
    for (const state of this.states.values()) {
      state.dispose();
    }
    this.states.clear();
    this.underlines.clear();
  }

  private applySettings(settings: SpellSettings): void {
    this.settings = settings;
    this.rejectSuggestions("Spell-check settings changed.");
    this.refreshDocuments();
  }

  private schedule(state: SpellModel): void {
    if (this.settings.enabled) {
      state.schedule(() => this.sendDocument(state), DOCUMENT_CHANGE_DELAY_MS);
    }
  }

  private sendDocument(state: SpellModel): void {
    const binding = currentEditorBinding();
    if (
      !this.settings.enabled ||
      binding === null ||
      binding.protocol !== "projection" ||
      !this.states.has(state.model)
    ) {
      return;
    }
    const documentRevision = ++this.documentRevision;
    state.markSubmitted(documentRevision);
    postToEditorBinding(binding, {
      type: "spell-document-changed",
      path: uriHostPath(state.model.uri),
      content: state.model.getValue(),
      documentRevision,
      ...editorAttribution(binding),
    });
  }

  private refreshDocuments(): void {
    for (const state of this.states.values()) {
      state.clear();
      this.schedule(state);
    }
    this.render();
  }

  private pathKey(state: SpellModel): string {
    return normalizePath(uriHostPath(state.model.uri));
  }

  private currentState(): SpellModel | null {
    const model = this.editor.getModel();
    return model === null ? null : (this.states.get(model) ?? null);
  }

  private render(): void {
    const state = this.settings.enabled ? this.currentState() : null;
    this.underlines.set(state?.decorations() ?? []);
  }

  private ownsProjection(message: SpellProjection): boolean {
    return ownsSpellProjection(currentEditorBinding(), message);
  }

  private rejectSuggestions(reason: string): void {
    for (const request of this.suggestions.values()) {
      request.reject(new Error(reason));
    }
    this.suggestions.clear();
  }

  private rejectAdditions(reason: string): void {
    for (const request of this.additions.values()) {
      request.reject(new Error(reason));
    }
    this.additions.clear();
  }

  private nextRequest(kind: string): string {
    return `spell-${kind}-${++this.requestSequence}`;
  }
}
