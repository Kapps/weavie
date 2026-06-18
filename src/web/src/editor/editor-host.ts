// The Monaco editor and the @codingame/monaco-vscode-api service layer behind it are by far the
// heaviest code in the app. This module is *dynamically imported* (see App.tsx onMount) so all of it
// lands in a separate chunk that loads AFTER the shell (the terminal panes) has painted — keeping the
// multi-megabyte editor code off the first-paint path. Everything Monaco-touching that the shell needs
// is reached through the EditorHost handle returned here.

import { log, postToHost } from "../bridge";
import { startLanguageServices } from "../lsp/lsp-client";
import { createEditor, monaco } from "./monaco-setup";
import { initEditorServices } from "./vscode-services";

// Editor-session state that must survive a Vite hot reload. A hot reload rebuilds the editor widget (App is
// the HMR boundary — see vscode-services.ts), but the open file *models* live in the global vscode model
// service and persist. Stash which file + view state was active so the rebuilt editor can reattach to the
// surviving model instead of coming up blank — making a hot reload seamless. Production never hot-reloads, so
// this is written once on teardown and read once on the next build.
declare global {
  interface Window {
    __WEAVIE_EDITOR_DOC__?:
      | {
          uri: string;
          viewState: monaco.editor.ICodeEditorViewState | null;
        }
      | undefined;
  }
}

/** Resolves after two animation frames — enough for Monaco to lay out and paint its first frame. */
function nextPaint(): Promise<void> {
  return new Promise((resolve) => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
  });
}

/** The live editor, plus the operations the shell drives it with (open a file, review a diff, tear down). */
export interface EditorHost {
  readonly editor: monaco.editor.IStandaloneCodeEditor;
  /** Loads a file's contents into the editor and reveals <paramref>line</paramref> (host open-file). */
  openFile(path: string, content: string, line: number): void;
  /**
   * Refreshes an already-open file's contents in place after an edit landed elsewhere (Claude editing the
   * file in any permission mode, or the diff Keep), preserving the scroll/cursor view state and never
   * stealing focus. No-op when the file has no model yet (its content lives on disk until first opened).
   */
  applyExternalEdit(path: string, content: string): void;
  /**
   * Returns the live `file://` model for <paramref>path</paramref>, creating it from <paramref>seed</paramref>
   * if absent (and wiring its autosave). The Changes view shares this model so its diff is the live buffer —
   * the caller MUST NOT dispose it; the model is owned by the host for the session.
   */
  getOrCreateFileModel(path: string, seed: string): monaco.editor.ITextModel;
  /**
   * Begins an inline review of an openDiff proposal: makes <paramref>path</paramref> the active editor
   * showing <paramref>proposed</paramref>, and suppresses autosave/refresh for it until <see cref="endReview"/>
   * (Claude writes the file on accept, not our autosave).
   */
  beginReview(path: string, proposed: string, line: number): void;
  /**
   * Ends an inline review and returns the file's final buffer content. When <paramref>keep</paramref> is
   * false the buffer is reverted to <paramref>original</paramref> first. Re-enables autosave for the file.
   */
  endReview(path: string, keep: boolean, original: string): string;
  /**
   * Tears the host down: flushes any pending autosave, drops all subscriptions (including those on models
   * that outlive the widget), and disposes the editor. The session's file models are intentionally NOT
   * disposed — they persist so the next host (e.g. after a hot reload) can reattach to them.
   */
  dispose(): void;
}

/** A real user file worth autosaving / reporting as active: a `file://` model (the editor's only models). */
function isUserFileModel(model: monaco.editor.ITextModel): boolean {
  return model.uri.scheme === "file";
}

/**
 * Brings up the editor: initializes the VSCode services (which must precede any editor creation),
 * creates the editor in <paramref>container</paramref>, and wires lazy per-language LSP. The caller
 * (App) catches failures so a broken editor never takes down the terminal panes.
 */
export async function createEditorHost(container: HTMLElement): Promise<EditorHost> {
  await initEditorServices();
  const editor = createEditor(container);

  // Don't yank focus if the user has already clicked into a terminal while the editor was loading —
  // only claim focus when nothing else has it (matches the old eager-focus when the shell first mounts).
  if (document.activeElement === null || document.activeElement === document.body) {
    editor.focus();
  }

  // Lazy per-language LSP via the bridge (no-op if the host didn't inject bridge config); a client
  // connects the first time a document of its language is open.
  startLanguageServices();

  // Tell the host which file + selection is active so the embedded Claude always knows what the user
  // is looking at (the host pushes a selection_changed notification + answers getCurrentSelection from
  // it). Debounced — cursor moves fire rapidly and Claude only needs the settled state. The scratch
  // sample doc is suppressed; it isn't a file the user is working on.
  let emitTimer: ReturnType<typeof setTimeout> | undefined;
  const emitActiveEditor = (): void => {
    const model = editor.getModel();
    if (model === null || !isUserFileModel(model)) {
      return;
    }
    const sel = editor.getSelection();
    const text = sel !== null && !sel.isEmpty() ? model.getValueInRange(sel) : "";
    // Monaco positions are 1-based; the IDE selection protocol is 0-based.
    postToHost({
      type: "active-editor-changed",
      uri: model.uri.toString(),
      languageId: model.getLanguageId(),
      text,
      selection: {
        start: {
          line: (sel?.startLineNumber ?? 1) - 1,
          character: (sel?.startColumn ?? 1) - 1,
        },
        end: { line: (sel?.endLineNumber ?? 1) - 1, character: (sel?.endColumn ?? 1) - 1 },
        isEmpty: text.length === 0,
      },
    });
  };
  const scheduleEmitActiveEditor = (): void => {
    if (emitTimer !== undefined) {
      clearTimeout(emitTimer);
    }
    emitTimer = setTimeout(emitActiveEditor, 150);
  };
  // Every subscription this host makes is collected here so dispose() can tear them ALL down — crucially
  // including listeners on models that OUTLIVE the widget (a hot reload rebuilds the editor but not the
  // models), so a rebuilt host never stacks a second set of autosave/selection handlers on a surviving model.
  const disposables: monaco.IDisposable[] = [
    editor.onDidChangeModel(scheduleEmitActiveEditor),
    editor.onDidChangeCursorSelection(scheduleEmitActiveEditor),
  ];

  // Autosave: the model is the working copy, debounce-flushed to disk so the embedded Claude (which reads
  // disk directly) sees the user's current state. Guards against echoing host-driven writes back as saves:
  //  - `applyingRemote` suppresses the synchronous change event fired during a programmatic setValue.
  //  - `lastApplied[path]` is the content we last loaded/saved, so a change that settles back to it (an
  //    open/refresh, or a save round-trip) doesn't re-schedule a write.
  // A user autosave fires no Claude hook, so it never loops back through the change tracker.
  const lastApplied = new Map<string, string>();
  const autosaveAttached = new WeakSet<monaco.editor.ITextModel>();
  const saveTimers = new Map<string, ReturnType<typeof setTimeout>>();
  // Content queued behind a debounce, kept so dispose() (incl. a hot reload) can flush it synchronously
  // rather than dropping an unsaved edit.
  const pendingSaves = new Map<string, string>();
  let applyingRemote = false;
  // Files whose buffer holds a pending openDiff proposal under inline review (default mode). Autosave AND
  // host refresh are suppressed for these — Claude is the one who writes on accept (FILE_SAVED); pre-writing
  // the proposal would double-write and, for a new file, collide ("file already exists").
  const pendingReview = new Set<string>();

  // Write a file's queued buffer to disk now (via the host), clearing its debounce. Called both by the
  // debounce timer and by dispose(), so a teardown flushes pending edits instead of losing them.
  const flushSave = (key: string): void => {
    const timer = saveTimers.get(key);
    if (timer !== undefined) {
      clearTimeout(timer);
      saveTimers.delete(key);
    }
    const content = pendingSaves.get(key);
    if (content === undefined) {
      return;
    }
    pendingSaves.delete(key);
    lastApplied.set(key, content);
    // The host write wants the native path, which the canonical URI's fsPath gives back.
    postToHost({ type: "save-buffer", path: monaco.Uri.parse(key).fsPath, content });
  };

  const attachAutosave = (model: monaco.editor.ITextModel): void => {
    if (autosaveAttached.has(model) || !isUserFileModel(model)) {
      return;
    }
    autosaveAttached.add(model);
    // Key state by the canonical URI string (stable + matching openFile/applyExternalEdit).
    const key = model.uri.toString();
    // Attached to the MODEL (not an editor), so a file shown in both the main editor and the Changes-view
    // diff doesn't double-schedule.
    disposables.push(
      model.onDidChangeContent(() => {
        if (applyingRemote || pendingReview.has(key)) {
          return;
        }
        const content = model.getValue();
        if (lastApplied.get(key) === content) {
          pendingSaves.delete(key);
          return;
        }
        pendingSaves.set(key, content);
        const delay = editor.getModel() === model ? 250 : 600;
        const pending = saveTimers.get(key);
        if (pending !== undefined) {
          clearTimeout(pending);
        }
        saveTimers.set(
          key,
          setTimeout(() => flushSave(key), delay),
        );
      }),
      model.onWillDispose(() => {
        const pending = saveTimers.get(key);
        if (pending !== undefined) {
          clearTimeout(pending);
          saveTimers.delete(key);
        }
        pendingSaves.delete(key);
      }),
    );
  };

  // The host owns file models for the session — created once, reused, kept live by autosave + refresh,
  // and never disposed by the Changes view that shares them.
  const ensureModel = (path: string, seed: string): monaco.editor.ITextModel => {
    const uri = monaco.Uri.file(path);
    let model = monaco.editor.getModel(uri);
    if (model === null) {
      model = monaco.editor.createModel(seed, undefined, uri);
      lastApplied.set(uri.toString(), seed);
    }
    attachAutosave(model);
    return model;
  };

  const getOrCreateFileModel = (path: string, seed: string): monaco.editor.ITextModel =>
    ensureModel(path, seed);

  const openFile = (path: string, content: string, line: number): void => {
    // Reveal the live model; if it already exists keep its (possibly dirty) buffer rather than clobbering
    // it with disk content — autosave + refresh keep it in sync. New models are seeded from disk content.
    const model = ensureModel(path, content);
    editor.setModel(model);
    editor.revealLineInCenter(line);
    editor.setPosition({ lineNumber: line, column: 1 });
    editor.focus();
  };

  const applyExternalEdit = (path: string, content: string): void => {
    const uri = monaco.Uri.file(path);
    const model = monaco.editor.getModel(uri);
    if (model === null || model.getValue() === content || pendingReview.has(uri.toString())) {
      return;
    }
    // Restore the view state around setValue so the refresh doesn't fling the visible file back to line 1;
    // only meaningful when this is the editor's active model. Mark the content applied + suppress the echo
    // so this host-driven write isn't bounced back as an autosave.
    const isActive = editor.getModel() === model;
    const viewState = isActive ? editor.saveViewState() : null;
    applyingRemote = true;
    lastApplied.set(uri.toString(), content);
    model.setValue(content);
    applyingRemote = false;
    if (viewState !== null) {
      editor.restoreViewState(viewState);
    }
  };

  // Programmatic content swap that never echoes as an autosave (used by review begin/revert).
  const setModelContentSilently = (
    model: monaco.editor.ITextModel,
    key: string,
    content: string,
  ): void => {
    if (model.getValue() === content) {
      lastApplied.set(key, content);
      return;
    }
    applyingRemote = true;
    lastApplied.set(key, content);
    model.setValue(content);
    applyingRemote = false;
  };

  const beginReview = (path: string, proposed: string, line: number): void => {
    const uri = monaco.Uri.file(path);
    // Mark pending BEFORE touching content so the proposal (and any tweaks during review) never autosave.
    pendingReview.add(uri.toString());
    const model = ensureModel(path, proposed);
    setModelContentSilently(model, uri.toString(), proposed);
    editor.setModel(model);
    editor.revealLineInCenter(Math.max(1, line));
    editor.focus();
  };

  const endReview = (path: string, keep: boolean, original: string): string => {
    const uri = monaco.Uri.file(path);
    const key = uri.toString();
    const model = monaco.editor.getModel(uri);
    if (model !== null && !keep) {
      // Reject/cancel: restore the file to its pre-proposal content.
      setModelContentSilently(model, key, original);
    }
    pendingReview.delete(key);
    // Sync the autosave baseline to the buffer's current value: on keep, the kept content is what Claude
    // writes (don't re-save it); on reject, it's `original`. Post-review user edits autosave from here.
    const finalContent = model?.getValue() ?? (keep ? "" : original);
    if (model !== null) {
      lastApplied.set(key, finalContent);
    }
    return finalContent;
  };

  // Snapshot the active file + view state for the next (hot-reloaded) host to restore. Only a real user file
  // is worth remembering; an empty editor clears it.
  const rememberActiveDoc = (): void => {
    const model = editor.getModel();
    window.__WEAVIE_EDITOR_DOC__ =
      model !== null && isUserFileModel(model)
        ? { uri: model.uri.toString(), viewState: editor.saveViewState() }
        : undefined;
  };

  const dispose = (): void => {
    rememberActiveDoc();
    for (const key of [...saveTimers.keys()]) {
      flushSave(key);
    }
    if (emitTimer !== undefined) {
      clearTimeout(emitTimer);
    }
    for (const subscription of disposables) {
      subscription.dispose();
    }
    editor.dispose();
  };

  // Hot-reload restore: the freshly-built widget comes up with no model. If a file was open before the reload
  // its model survived in the global model service — reattach it with its scroll/cursor so the reload is
  // seamless rather than blank. Re-wire autosave and seed the baseline to the surviving buffer so the reattach
  // itself doesn't bounce back as a redundant save.
  const lastDoc = window.__WEAVIE_EDITOR_DOC__;
  if (lastDoc !== undefined) {
    const restored = monaco.editor.getModel(monaco.Uri.parse(lastDoc.uri));
    if (restored !== null) {
      attachAutosave(restored);
      lastApplied.set(lastDoc.uri, restored.getValue());
      editor.setModel(restored);
      if (lastDoc.viewState !== null) {
        editor.restoreViewState(lastDoc.viewState);
      }
    }
  }

  // Wait for Monaco's first real paint before resolving. The caller fades the splash on resolution, so
  // this keeps the editor's initial layout/paint hidden under the splash rather than flashing into view.
  await nextPaint();

  log("info", "editor host ready");
  return {
    editor,
    openFile,
    applyExternalEdit,
    getOrCreateFileModel,
    beginReview,
    endReview,
    dispose,
  };
}
