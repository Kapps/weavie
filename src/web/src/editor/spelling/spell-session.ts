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
import { uriHostPath } from "../fs-path";
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
  SpellCheckResult,
  SpellContext,
  SpellDictionaryChanged,
  SpellMenuTarget,
  SpellRestoreResult,
  SpellScope,
  SpellSuggestResult,
} from "./spell-types";

export type {
  SpellAddWordResult,
  SpellCheckResult,
  SpellContext,
  SpellDictionaryChanged,
  SpellMenuTarget,
  SpellRestoreResult,
  SpellScope,
  SpellSuggestResult,
} from "./spell-types";

interface CheckRequest {
  state: SpellModel;
  modelEpoch: string;
  locale: string;
  lines: Map<string, string>;
}

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

interface RestoreRequest {
  state: SpellModel;
  modelEpoch: string;
}

const CHECK_DELAY_MS = 150;

/** Coordinates Monaco's authored-line anchors with Core's spell-check and dictionary protocol. */
export class SpellSession {
  private readonly states = new Map<monaco.editor.ITextModel, SpellModel>();
  private readonly underlines: monaco.editor.IEditorDecorationsCollection;
  private readonly checks = new Map<string, CheckRequest>();
  private readonly suggestions = new Map<string, SuggestRequest>();
  private readonly additions = new Map<string, AddRequest>();
  private readonly restores = new Map<string, RestoreRequest>();
  private readonly settingsSubscription: () => void;
  private requestSequence = 0;
  private anchorSequence = 0;
  private epochSequence = 0;
  private settings = currentSpellSettings();

  constructor(private readonly editor: monaco.editor.IStandaloneCodeEditor) {
    this.underlines = editor.createDecorationsCollection();
    this.settingsSubscription = onSpellSettingsChanged((settings) => this.applySettings(settings));
    editor.onDidChangeModel(() => this.render());
  }

  /** Starts tracking one real file model; reloads restore matching authored lines while dirty changes mark new ones. */
  track(model: monaco.editor.ITextModel, isDirty: () => boolean): void {
    if (this.states.has(model)) {
      return;
    }
    const state = new SpellModel(
      model,
      () => this.nextEpoch(),
      () => `spell-anchor-${++this.anchorSequence}`,
    );
    this.states.set(model, state);
    state.track(
      isDirty,
      () => {
        this.render();
        this.schedule(state);
      },
      () => {
        this.render();
        this.requestRestore(state);
      },
      () => {
        this.forgetRestores(state);
        this.states.delete(model);
        this.render();
      },
    );
    this.requestRestore(state);
  }

  /** A session handoff starts quiet even when a Monaco working copy happens to survive the swap. */
  clearProjection(): void {
    for (const state of this.states.values()) {
      state.clear();
    }
    this.checks.clear();
    this.restores.clear();
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

  requestSuggestions(context: SpellContext): Promise<string[]> {
    const state = this.currentState();
    const binding = currentEditorBinding();
    if (
      state === null ||
      binding === null ||
      binding.protocol !== "projection" ||
      !state.isCurrentContext(context)
    ) {
      return Promise.reject(new Error("The spelling target is no longer current."));
    }
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

  handleCheckResult(message: SpellCheckResult): void {
    const request = this.checks.get(message.requestId);
    this.checks.delete(message.requestId);
    if (
      request === undefined ||
      !this.ownsProjection(message) ||
      request.modelEpoch !== message.modelEpoch ||
      request.locale !== message.locale ||
      message.locale !== this.settings.locale ||
      request.state.modelEpoch !== message.modelEpoch
    ) {
      return;
    }
    if (message.error !== undefined) {
      notify("warn", `Spell check failed: ${message.error}`, "spell-check");
      return;
    }
    const accepted = new Set<string>();
    for (const [anchorId, text] of request.lines) {
      if (
        request.state.latestRequest.get(anchorId) === message.requestId &&
        request.state.anchorText(anchorId) === text
      ) {
        accepted.add(anchorId);
        request.state.issues.delete(anchorId);
      }
    }
    for (const issue of message.issues) {
      if (!accepted.has(issue.anchorId) || !request.state.validIssue(issue)) {
        continue;
      }
      const issues = request.state.issues.get(issue.anchorId) ?? [];
      issues.push(issue);
      request.state.issues.set(issue.anchorId, issues);
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
    if (message.error !== undefined) {
      request.reject(new Error(message.error));
      return;
    }
    notify("info", `Added “${request.word}” to the ${request.scope} dictionary.`);
    request.resolve();
  }

  handleRestoreResult(message: SpellRestoreResult): void {
    const request = this.restores.get(message.requestId);
    this.restores.delete(message.requestId);
    if (
      request === undefined ||
      !this.ownsProjection(message) ||
      request.modelEpoch !== message.modelEpoch ||
      request.state.modelEpoch !== message.modelEpoch
    ) {
      return;
    }
    for (const anchorId of request.state.restoreAuthoredLines(message.lines)) {
      request.state.needsCheck.add(anchorId);
    }
    this.schedule(request.state);
    this.render();
  }

  handleDictionaryChanged(message: SpellDictionaryChanged): void {
    if (!this.ownsProjection(message)) {
      return;
    }
    this.checks.clear();
    this.rejectSuggestions("The dictionary changed.");
    for (const state of this.states.values()) {
      for (const anchorId of state.anchors.keys()) {
        state.needsCheck.add(anchorId);
      }
      this.schedule(state);
    }
  }

  dispose(): void {
    this.settingsSubscription();
    this.clearProjection();
    for (const state of this.states.values()) {
      state.dispose();
    }
    this.states.clear();
    this.underlines.clear();
  }

  private applySettings(settings: SpellSettings): void {
    this.settings = settings;
    this.checks.clear();
    this.restores.clear();
    this.rejectSuggestions("Spell-check settings changed.");
    for (const state of this.states.values()) {
      state.invalidate();
      if (settings.enabled) {
        this.requestRestore(state);
        for (const anchorId of state.anchors.keys()) {
          state.needsCheck.add(anchorId);
        }
        this.schedule(state);
      }
    }
    this.render();
  }

  private schedule(state: SpellModel): void {
    if (this.settings.enabled && state.needsCheck.size > 0) {
      state.schedule(() => this.check(state), CHECK_DELAY_MS);
    }
  }

  private check(state: SpellModel): void {
    const binding = currentEditorBinding();
    if (
      !this.settings.enabled ||
      binding === null ||
      binding.protocol !== "projection" ||
      state.needsCheck.size === 0
    ) {
      return;
    }
    const lines = new Map<string, string>();
    for (const anchorId of state.needsCheck) {
      const text = state.anchorText(anchorId);
      if (text !== null) {
        lines.set(anchorId, text);
      }
    }
    state.needsCheck.clear();
    if (lines.size === 0) {
      return;
    }
    const requestId = this.nextRequest("check");
    for (const anchorId of lines.keys()) {
      state.latestRequest.set(anchorId, requestId);
    }
    this.checks.set(requestId, {
      state,
      modelEpoch: state.modelEpoch,
      locale: this.settings.locale,
      lines,
    });
    postToEditorBinding(binding, {
      type: "spell-check",
      requestId,
      modelEpoch: state.modelEpoch,
      path: uriHostPath(state.model.uri),
      languageId: state.model.getLanguageId(),
      lines: [...lines].map(([anchorId, text]) => ({ anchorId, text })),
      ...editorAttribution(binding),
    });
  }

  private requestRestore(state: SpellModel): void {
    const binding = currentEditorBinding();
    if (!this.settings.enabled || binding === null || binding.protocol !== "projection") {
      return;
    }
    this.forgetRestores(state);
    const requestId = this.nextRequest("restore");
    this.restores.set(requestId, { state, modelEpoch: state.modelEpoch });
    postToEditorBinding(binding, {
      type: "spell-restore",
      requestId,
      modelEpoch: state.modelEpoch,
      path: uriHostPath(state.model.uri),
      ...editorAttribution(binding),
    });
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

  private forgetRestores(state: SpellModel): void {
    for (const [requestId, request] of this.restores) {
      if (request.state === state) {
        this.restores.delete(requestId);
      }
    }
  }

  private nextRequest(kind: string): string {
    return `spell-${kind}-${++this.requestSequence}`;
  }

  private nextEpoch(): string {
    return `spell-model-${++this.epochSequence}`;
  }
}
