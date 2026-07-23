// Monaco + the monaco-vscode-api service layer are the heaviest code in the app, so this module is dynamically
// imported (App.tsx onMount) into a chunk that loads after the shell paints. The shell reaches everything
// Monaco-touching through the EditorHost handle.
//
// File models are real VSCode working copies (one per URI, reused): opened via createModelReference through the
// host-backed file:// provider, saved through it on weavie's debounce, reloaded on fs-change. See
// host-file-provider.ts and docs/specs/file-management-and-sessions.md.

import {
  getService,
  ITextFileService,
  ITextModelService,
} from "@codingame/monaco-vscode-api/services";
import {
  currentEditorBinding,
  type EditorBinding,
  editorAttribution,
  log,
  postToEditorBinding,
} from "../bridge";
import { startLanguageServices } from "../lsp/lsp-client";
import { installReferenceCommands } from "../lsp/reference-commands";
import { installTestLenses } from "../tests/test-lens";
import { installAltClickPeek } from "./alt-click-peek";
import { setDirtyPath } from "./dirty-store";
import { setEditorStatus } from "./editor-status-store";
import { canonicalFsPath, uriHostPath } from "./fs-path";
import { mediaTypeOf } from "./media/media-types";
import { createEditor, monaco } from "./monaco-setup";
import { leaveLine } from "./nav-history";
import { captureViewState, editorSession, openTab, promote } from "./session-store";
import {
  type SpellAddWordResult,
  type SpellCheckResult,
  type SpellContext,
  type SpellDictionaryChanged,
  type SpellMenuTarget,
  type SpellRestoreResult,
  type SpellScope,
  SpellSession,
  type SpellSuggestResult,
} from "./spelling/spell-session";
import { initEditorServices, setOpenEditorSink } from "./vscode-services";

// A resolved, refcounted model reference held for an open file. Disposing it drops a refcount; the model is
// freed only when no reference remains, so a feature's transient createModelReference never frees ours.
type ModelRef = Awaited<ReturnType<ITextModelService["createModelReference"]>>;

// Scheme for the transient openDiff review model. Not `file://`, so it's never a working copy, never resolved
// by the host file provider, and never the active editor — a review can't dirty or collide with the real file.
const REVIEW_SCHEME = "weavie-review";

// Open working copies must survive a Vite hot reload, which rebuilds the widget but not the global VSCode
// services. Keep their model references alive on `window` so the new host re-adopts them and the refcount never
// hits 0 (unsaved edits survive, no disk re-read). Dev-only.
declare global {
  interface Window {
    __WEAVIE_EDITOR_REFS__?: Map<string, ModelRef>;
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
  spellContextAtClientPoint(clientX: number, clientY: number): SpellContext | null;
  spellContextAtCursor(): SpellMenuTarget | null;
  requestSpellSuggestions(context: SpellContext): Promise<string[]>;
  applySpellSuggestion(args: unknown): boolean;
  addSpellWord(scope: SpellScope, args: unknown): Promise<void>;
  handleSpellCheckResult(message: SpellCheckResult): void;
  handleSpellSuggestResult(message: SpellSuggestResult): void;
  handleSpellAddWordResult(message: SpellAddWordResult): void;
  handleSpellRestoreResult(message: SpellRestoreResult): void;
  handleSpellDictionaryChanged(message: SpellDictionaryChanged): void;
  clearSpellProjection(): void;
  /**
   * Switches the editor to a file working copy; `placement` reveals a 1-based `line` (optionally at `column`;
   * `focus: false` reveals without stealing focus) or restores the tab's saved view state. Resolves `true` when
   * shown (or superseded by a newer open), `false` when unreadable — the host has already toasted, and the
   * caller rolls its now-broken tab back.
   */
  show(
    path: string,
    placement: { line: number; column?: number; focus?: boolean } | { viewState: unknown },
  ): Promise<boolean>;
  /**
   * Closes a tab: flushes its pending save, then releases its refcounted working-copy reference (the only site
   * that disposes one, never dispose()). The caller must switch the editor off this model first. Pass `discard`
   * to skip the flush (a scratch buffer being discarded/converted, whose temp file is deleted host-side).
   */
  closeFile(path: string, discard?: boolean): void;
  /** The current text of an open file's working copy (for a scratch save / discard check), or undefined. */
  contentOf(path: string): string | undefined;
  /** Cancels a file's pending debounced save (so no autosave fires while a scratch save dialog is open). */
  cancelSave(path: string): void;
  /**
   * Flushes a file's pending debounced save and resolves once it lands, so a host action that reads the file
   * next (a per-hunk revert's guard) sees current content. No-op when not dirty.
   */
  flush(path: string): Promise<void>;
  /**
   * Flushes every dirty working copy to its editor-owning backend and resolves once all saves land. Called
   * before a cross-backend switch so unsaved edits persist on their own host.
   */
  flushDirty(): Promise<void>;
  /** Clears the editor to an empty pane (the last tab was closed). */
  clear(): void;
  /**
   * Rebinds the editor to the (already-updated) session store after a switch: releases the previous session's
   * working copies, then reopens the new active tab (non-active tabs reopen lazily).
   */
  rebindSession(): Promise<void>;
  /**
   * Begins an inline review of an openDiff proposal in a transient model (the working copy is left untouched),
   * shows `proposed` revealed at 1-based `line`, and returns the model's URI so the caller renders the diff over it.
   */
  beginReview(path: string, proposed: string, line: number): string;
  /**
   * Ends an inline review and returns the proposal's final content. Restores the editor off the review model:
   * to the file's working copy when open, else a kept proposal keeps showing, a rejected one returns to the
   * prior view. Disposes the review model.
   */
  endReview(path: string, keep: boolean, original: string): string;
  /**
   * Tears the host down: flushes pending saves, drops all subscriptions (including on models that outlive the
   * widget), disposes the editor. Working copies and references persist on window so the next host reattaches.
   */
  dispose(): void;
}

/** A real user file worth saving / reporting as active: a `file://` model (the editor's working copies). */
function isUserFileModel(model: monaco.editor.ITextModel): boolean {
  return model.uri.scheme === "file";
}

/**
 * Brings up the editor: initializes the VSCode services (must precede editor creation), creates the editor in
 * `container`, wires lazy per-language LSP. `onSaveError` / `onOpenError` surface a failed save / open as a
 * toast so neither strands silently. `onLeaveViewport` records the outgoing file's scrolled-to position as a
 * navigation point on a cross-file jump (see showFile), so Back returns there.
 */
export async function createEditorHost(
  container: HTMLElement,
  onSaveError?: (message: string) => void,
  onOpenError?: (message: string) => void,
  onLeaveViewport?: (loc: { path: string; line: number }) => void,
): Promise<EditorHost> {
  await initEditorServices();
  const textModelService = await getService(ITextModelService);
  const textFileService = await getService(ITextFileService);
  const editor = createEditor(container);
  const spelling = new SpellSession(editor);

  // Open file working copies survive a hot reload on this window-scoped map; first host creates it.
  window.__WEAVIE_EDITOR_REFS__ ??= new Map<string, ModelRef>();
  const refs = window.__WEAVIE_EDITOR_REFS__;

  // Don't yank focus if the user has already clicked into a terminal while the editor was loading: only claim
  // focus when nothing else has it.
  if (document.activeElement === null || document.activeElement === document.body) {
    editor.focus();
  }

  // Lazy per-language LSP via the bridge (no-op without bridge config); a client connects the first time a
  // document of its language is open. Fire-and-forget: services are already up here (we just awaited them), so
  // its internal initEditorServices() await resolves at once.
  void startLanguageServices();

  // Run-test lenses on test files (fed by LSP document symbols + the workspace test profile). Idempotent —
  // guarded against a second install across a hot reload.
  installTestLenses();

  // Bridge the LSP "N Reference(s)" CodeLens commands to Monaco's references peek (idempotent). Otherwise the
  // click no-ops: csharp-ls sends an unregistered command id, and the TypeScript ecosystem sends args Monaco's
  // built-in handler can't consume.
  installReferenceCommands();

  // Tell the host which file + selection is active so embedded Claude knows what the user is looking at.
  // Debounced (cursor moves fire rapidly); the transient review model is suppressed — not a file being worked on.
  let emitTimer: ReturnType<typeof setTimeout> | undefined;
  const emitActiveEditor = (binding: EditorBinding): void => {
    if (currentEditorBinding() !== binding) {
      return;
    }
    const model = editor.getModel();
    if (model === null || !isUserFileModel(model)) {
      return;
    }
    const sel = editor.getSelection();
    const text = sel !== null && !sel.isEmpty() ? model.getValueInRange(sel) : "";
    // Monaco positions are 1-based; the IDE selection protocol is 0-based.
    postToEditorBinding(binding, {
      type: "active-editor-changed",
      uri: model.uri.toString(),
      languageId: model.getLanguageId(),
      text,
      // Stamp the owning session so a selection emit that fires after a switch is attributed correctly.
      ...editorAttribution(binding),
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
    const binding = currentEditorBinding();
    if (binding !== null) {
      emitTimer = setTimeout(() => emitActiveEditor(binding), 150);
    }
  };

  // Drive the editor status footer (cursor/selection/EOL). Written synchronously — the footer wants immediate
  // cursor feedback, unlike the debounced host emit above. Null when no real file model is showing.
  const updateStatus = (): void => {
    const model = editor.getModel();
    const position = editor.getPosition();
    if (model === null || !isUserFileModel(model) || position === null) {
      setEditorStatus(null);
      return;
    }
    let selectionCount = 0;
    for (const sel of editor.getSelections() ?? []) {
      selectionCount += model.getValueInRange(sel).length;
    }
    setEditorStatus({
      line: position.lineNumber,
      column: position.column,
      selectionCount,
      eol: model.getEndOfLineSequence() === monaco.editor.EndOfLineSequence.CRLF ? "CRLF" : "LF",
    });
  };

  // Reflect the SETTLED active model onto the container as data-active-file — the only signal for WHICH file the
  // editor is actually showing now. The tab's active state and the optimistic currentFile both flip before the
  // async model swap (a host round-trip) lands, so neither can stand in for it. Drives e2e waits and doubles as
  // a debugging aid; cleared for the review model, overlay tabs, and the empty pane (no file model).
  const reflectActiveFile = (): void => {
    const model = editor.getModel();
    if (model !== null && isUserFileModel(model)) {
      container.dataset.activeFile = model.uri.fsPath;
    } else {
      delete container.dataset.activeFile;
    }
  };

  // Every subscription is collected so dispose() tears them all down — including listeners on models that
  // outlive the widget, so a rebuilt host never stacks a second handler set on a surviving model.
  const disposables: monaco.IDisposable[] = [
    editor.onDidChangeModel(scheduleEmitActiveEditor),
    editor.onDidChangeCursorSelection(scheduleEmitActiveEditor),
    // onDidChangeCursorSelection fires on every caret move too, so it covers both cursor and selection updates.
    editor.onDidChangeCursorSelection(updateStatus),
    editor.onDidChangeModel(updateStatus),
    editor.onDidChangeModel(reflectActiveFile),
    installAltClickPeek(editor),
  ];

  // Mirror each working copy's dirty state into the dirty store so the tab strip shows an unsaved `*` (the error
  // gate below can hold a flush back). Seed from in-memory models (covers a hot reload), then track changes.
  for (const model of textFileService.files.models) {
    setDirtyPath(model.resource.fsPath, model.isDirty());
  }
  disposables.push(
    textFileService.files.onDidChangeDirty((model) => {
      setDirtyPath(model.resource.fsPath, model.isDirty());
    }),
  );

  // Keep the active tab's Monaco view state (scroll/cursor/folding) fresh in the session store so a relaunch /
  // hot reload reopens at the same position. Data-only (captureViewState never changes the active tab or order,
  // so no capture↔show loop); only real file working copies, debounced.
  let viewStateTimer: ReturnType<typeof setTimeout> | undefined;
  const snapshotViewState = (): void => {
    const model = editor.getModel();
    if (model === null || !isUserFileModel(model)) {
      return;
    }
    captureViewState(model.uri.fsPath, editor.saveViewState() ?? null);
  };
  const scheduleSnapshotViewState = (): void => {
    if (viewStateTimer !== undefined) {
      clearTimeout(viewStateTimer);
    }
    viewStateTimer = setTimeout(snapshotViewState, 200);
  };
  disposables.push(
    editor.onDidChangeCursorSelection(scheduleSnapshotViewState),
    editor.onDidScrollChange(scheduleSnapshotViewState),
  );

  // Save: debounce-flush the working copy to disk so embedded Claude (which reads disk) sees current state. A
  // blind overwrite (ignoreModifiedSince) — weavie's buffer is authoritative; the isDirty guard skips no-op saves.
  const saveAttached = new WeakSet<monaco.editor.ITextModel>();
  const saveTimers = new Map<string, ReturnType<typeof setTimeout>>();

  // Error gate (clean → erroring): hold a flush while the file shows error markers AND its last saved state was
  // clean, up to ERROR_HOLD_MS, so a just-broken edit doesn't hit disk where Claude reads it. Already-erroring
  // files save normally; a held edit releases the moment errors clear (onDidChangeMarkers below). Best-effort:
  // LSP diagnostics lag keystrokes, so a flush can still race ahead of a just-broken edit.
  const ERROR_HOLD_MS = 1500;
  // Whether the last flush persisted erroring content (drives the clean → erroring test); and, per held key,
  // when withholding began.
  const savedHadErrors = new Map<string, boolean>();
  const holdingSince = new Map<string, number>();

  const hasErrors = (uri: monaco.Uri): boolean =>
    monaco.editor
      .getModelMarkers({ resource: uri })
      .some((marker) => marker.severity === monaco.MarkerSeverity.Error);

  // Drop a key's pending debounced save and release any error-hold, leaving nothing to fire later.
  const cancelPendingSave = (key: string): void => {
    const timer = saveTimers.get(key);
    if (timer !== undefined) {
      clearTimeout(timer);
      saveTimers.delete(key);
    }
    holdingSince.delete(key);
  };

  // Surface a failed working-copy save to both the dev log and the user-facing toast (onSaveError).
  const reportSaveError = (key: string, error: unknown): void => {
    const name = key.split("/").pop() ?? key;
    const message = `Couldn't save ${name}: ${String(error)}`;
    log("error", message);
    onSaveError?.(message);
  };

  const flushSave = (key: string): void => {
    const timer = saveTimers.get(key);
    if (timer !== undefined) {
      clearTimeout(timer);
      saveTimers.delete(key);
    }
    const uri = monaco.Uri.parse(key);
    if (!textFileService.isDirty(uri)) {
      holdingSince.delete(key);
      return;
    }
    const errored = hasErrors(uri);
    if (errored && !(savedHadErrors.get(key) ?? false)) {
      // Clean → erroring: hold the flush, bounded by ERROR_HOLD_MS. onDidChangeMarkers retries when errors
      // clear; this timer is the fallback that saves anyway if they don't.
      const since = holdingSince.get(key) ?? Date.now();
      holdingSince.set(key, since);
      const elapsed = Date.now() - since;
      if (elapsed < ERROR_HOLD_MS) {
        saveTimers.set(
          key,
          setTimeout(() => flushSave(key), ERROR_HOLD_MS - elapsed),
        );
        return;
      }
    }
    holdingSince.delete(key);
    savedHadErrors.set(key, errored);
    void textFileService
      .save(uri, { ignoreModifiedSince: true, ignoreErrorHandler: true })
      .catch((error: unknown) => reportSaveError(key, error));
  };

  // Release a held flush as soon as a file's errors clear, rather than waiting for the next keystroke or the
  // ERROR_HOLD_MS fallback. Only touches files being held, so it's cheap on every marker update.
  disposables.push(
    monaco.editor.onDidChangeMarkers((resources) => {
      for (const resource of resources) {
        const key = resource.toString();
        if (holdingSince.has(key) && !hasErrors(resource)) {
          flushSave(key);
        }
      }
    }),
  );

  const attachSave = (model: monaco.editor.ITextModel): void => {
    if (!isUserFileModel(model)) {
      return;
    }
    spelling.track(model, () => textFileService.isDirty(model.uri));
    if (saveAttached.has(model)) {
      return;
    }
    saveAttached.add(model);
    const key = model.uri.toString();
    disposables.push(
      model.onDidChangeContent(() => {
        // Only a real user edit dirties the working copy; a host-driven reload/revert doesn't, so skip it.
        if (!textFileService.isDirty(model.uri)) {
          return;
        }
        // A real edit promotes a preview tab to persistent (no-op once persistent).
        promote(model.uri.fsPath);
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
        holdingSince.delete(key);
        savedHadErrors.delete(key);
      }),
    );
  };

  // A newly-created reference is private until its open wins. Concurrent opens can therefore dispose their own
  // superseded candidates without invalidating the reference adopted by a newer open of the same URI.
  const resolveRef = async (uri: monaco.Uri): Promise<{ ref: ModelRef; owned: boolean }> => {
    const key = uri.toString();
    const existing = refs.get(key);
    if (existing !== undefined) {
      return { ref: existing, owned: false };
    }
    return { ref: await textModelService.createModelReference(uri), owned: true };
  };

  // The single path that swaps the editor to a file working copy (open + restore differ only in `placement`).
  // setModel fires onDidChangeModel, driving the active-editor notification and currentFile tracking by
  // construction. Async opens use openSeq so the latest wins and a slow resolve can't clobber a newer open.
  let openSeq = 0;
  const showFile = async (
    uri: monaco.Uri,
    placement:
      | { line: number; column?: number; focus?: boolean }
      | { selection: monaco.IRange }
      | { viewState: monaco.editor.ICodeEditorViewState | null },
  ): Promise<boolean> => {
    const binding = currentEditorBinding();
    // Snapshot the outgoing tab's position before swapping away (data-only store write; never loops back).
    snapshotViewState();
    // On a cross-file jump, if the user scrolled the outgoing cursor off-screen, record where they were looking
    // as a nav point first (else Back skips it) — read before setModel, while the outgoing scroll is still live.
    const leaving = editor.getModel();
    const cursor = editor.getPosition();
    const visible = editor.getVisibleRanges();
    const top = visible[0];
    const bottom = visible[visible.length - 1];
    if (
      onLeaveViewport !== undefined &&
      leaving !== null &&
      cursor !== null &&
      top !== undefined &&
      bottom !== undefined &&
      isUserFileModel(leaving) &&
      leaving.uri.toString() !== uri.toString()
    ) {
      const line = leaveLine(cursor.lineNumber, top.startLineNumber, bottom.endLineNumber);
      if (line !== undefined) {
        onLeaveViewport({ path: uriHostPath(leaving.uri), line });
      }
    }
    const token = ++openSeq;
    try {
      const resolved = await resolveRef(uri);
      let ref = resolved.ref;
      if (binding !== currentEditorBinding() || token !== openSeq) {
        if (resolved.owned) {
          ref.dispose();
        }
        return true; // superseded by a newer open — that open owns the editor; not this tab's failure
      }
      if (resolved.owned) {
        const existing = refs.get(uri.toString());
        if (existing === undefined) {
          refs.set(uri.toString(), ref);
        } else {
          ref.dispose();
          ref = existing;
        }
      }
      attachSave(ref.object.textEditorModel);
      editor.setModel(ref.object.textEditorModel);
      if ("line" in placement) {
        const position = { lineNumber: placement.line, column: placement.column ?? 1 };
        editor.revealPositionInCenter(position);
        editor.setPosition(position);
        // focus: false = reveal only (the search panel's live preview keeps typing in its own input).
        if (placement.focus !== false) {
          editor.focus();
        }
      } else if ("selection" in placement) {
        editor.setSelection(placement.selection);
        editor.revealRangeInCenterIfOutsideViewport(placement.selection);
        editor.focus();
      } else if (placement.viewState !== null) {
        editor.restoreViewState(placement.viewState);
      }
      return true;
    } catch (error) {
      // A genuine read failure. If a newer open superseded this one, stay quiet — it owns the editor. Otherwise
      // error loudly: the model never swapped, so without this the tab would sit blank with no signal.
      if (binding !== currentEditorBinding() || token !== openSeq) {
        return true;
      }
      log("error", `open failed for ${uri.toString()}: ${String(error)}`);
      const name = uri.path.split("/").pop() ?? uri.path;
      onOpenError?.(`Couldn't open ${name}: ${String(error)}`);
      return false;
    }
  };

  const show = (
    path: string,
    placement: { line: number; column?: number; focus?: boolean } | { viewState: unknown },
  ): Promise<boolean> => {
    const resolved =
      "line" in placement
        ? placement
        : { viewState: placement.viewState as monaco.editor.ICodeEditorViewState | null };
    return showFile(monaco.Uri.file(canonicalFsPath(path)), resolved);
  };

  // Close a tab: flush any pending save, then release the refcounted reference (only site that disposes one;
  // never dispose(), since a hot reload keeps copies alive on window). Caller switches the editor off first.
  const closeFile = (path: string, discard = false): void => {
    const key = monaco.Uri.file(canonicalFsPath(path)).toString();
    if (discard) {
      // Discarded/converted scratch: drop the pending save instead of flushing — its temp file is being
      // deleted host-side, so a flush would be wasted or re-create the file.
      cancelPendingSave(key);
    } else {
      flushSave(key);
    }
    const ref = refs.get(key);
    if (ref !== undefined) {
      ref.dispose();
      refs.delete(key);
    }
    // Disposing a model doesn't reliably fire onDidChangeDirty(false), so a discarded (or error-held) dirty
    // file would leave a phantom `*` in the dirty store that resurrects on reopen. Clear it explicitly.
    setDirtyPath(monaco.Uri.parse(key).fsPath, false);
  };

  // The current text of an open working copy (seeds a scratch "save as" and decides whether a scratch close
  // needs a discard confirm). Undefined when the file isn't open as a working copy.
  const contentOf = (path: string): string | undefined => {
    const key = monaco.Uri.file(canonicalFsPath(path)).toString();
    return refs.get(key)?.object.textEditorModel.getValue();
  };

  // Flush a file's pending save and await it landing on disk. Used before a per-hunk revert so the host's
  // optimistic-concurrency guard reads current content, not a version the debounce hasn't written. No-op when clean.
  const flush = async (path: string): Promise<void> => {
    const key = monaco.Uri.file(canonicalFsPath(path)).toString();
    cancelPendingSave(key);
    const uri = monaco.Uri.parse(key);
    if (!textFileService.isDirty(uri)) {
      return;
    }
    await textFileService.save(uri, { ignoreModifiedSince: true, ignoreErrorHandler: true });
  };

  // Cancel a file's pending debounced save. Called before opening the native scratch save dialog so an
  // in-flight autosave can't re-create the temp file after the host has saved + deleted it.
  const cancelSave = (path: string): void => {
    cancelPendingSave(monaco.Uri.file(canonicalFsPath(path)).toString());
  };

  const clear = (): void => {
    openSeq += 1;
    editor.setModel(null);
  };

  // Release every open working copy (flush, drop reference, empty the editor). Used by rebindSession; unlike
  // dispose() this releases the refs, since a session switch (not a hot reload) must tear the old models down.
  const releaseAll = (): void => {
    // A rebind to media/web/source/empty opens no successor model, so it must invalidate an older async text
    // open explicitly. The immutable binding check above also keeps its eventual reference out of this session.
    openSeq += 1;
    for (const [key, ref] of [...refs]) {
      flushSave(key);
      ref.dispose();
      refs.delete(key);
      // Clear any lingering dirty flag (a model held back by the error gate stays dirty); the global dirty
      // store isn't session-scoped, so an uncleared path would outlive the switch.
      setDirtyPath(monaco.Uri.parse(key).fsPath, false);
    }
    editor.setModel(null);
  };

  // Flush every dirty working copy and resolve once all saves land. File writes stay pinned to the editor
  // owner; finishing before a cross-backend rebind lets the outgoing models be released without losing edits.
  // Individual save failures surface as toasts and don't block the others.
  const flushDirty = async (): Promise<void> => {
    const saves: Promise<void>[] = [];
    for (const key of [...refs.keys()]) {
      cancelPendingSave(key);
      const uri = monaco.Uri.parse(key);
      if (!textFileService.isDirty(uri)) {
        continue;
      }
      saves.push(
        textFileService.save(uri, { ignoreModifiedSince: true, ignoreErrorHandler: true }).then(
          () => undefined,
          (error: unknown) => reportSaveError(key, error),
        ),
      );
    }
    await Promise.all(saves);
  };

  // Route the editor service's file-opens (go-to-def / peek / references) through the tab store as a preview
  // open, then reveal the target range, so navigating reuses the one preview slot instead of piling up tabs.
  setOpenEditorSink((uri, selection) => {
    // Only real files are working copies. The transient `weavie-review:` model has no file provider, so an
    // editor-service open of one (e.g. go-to-def while a review shows) is a no-op: it's already on screen via
    // beginReview and must never become a tab or working copy.
    if (uri.scheme !== "file") {
      return;
    }
    // uriHostPath, not fsPath: the tab path is persisted host-side, so it must be host-native, not client-OS.
    openTab(uriHostPath(uri), { preview: true });
    void showFile(uri, selection !== undefined ? { selection } : { line: 1 });
  });

  // Review uses a transient model per file path (one openDiff is live at a time). Tracked so endReview can
  // read its final content and dispose it.
  const reviewModels = new Map<string, monaco.editor.ITextModel>();
  // What was showing before the review began, to restore on resolve when we can't show the real file.
  let preReview:
    | {
        model: monaco.editor.ITextModel | null;
        viewState: monaco.editor.ICodeEditorViewState | null;
      }
    | undefined;

  const beginReview = (path: string, proposed: string, line: number): string => {
    const fileUri = monaco.Uri.file(canonicalFsPath(path));
    // A non-file URI whose path keeps the real filename, so Monaco infers the language from the extension
    // while the scheme keeps it out of the file-service / working-copy world.
    const reviewUri = monaco.Uri.from({ scheme: REVIEW_SCHEME, path: fileUri.path });
    let model = monaco.editor.getModel(reviewUri);
    if (model === null) {
      model = monaco.editor.createModel(proposed, undefined, reviewUri);
    } else {
      model.setValue(proposed);
    }
    reviewModels.set(path, model);
    // Invalidate any in-flight async open so its late setModel can't clobber this review model — matters when
    // the host re-renders a held diff right after a session switch while restoreSession is still resolving.
    openSeq += 1;
    preReview = { model: editor.getModel(), viewState: editor.saveViewState() };
    editor.setModel(model);
    editor.revealLineInCenter(Math.max(1, line));
    editor.focus();
    return reviewUri.toString();
  };

  const endReview = (path: string, keep: boolean, original: string): string => {
    const fileUri = monaco.Uri.file(canonicalFsPath(path));
    const reviewModel = reviewModels.get(path);
    reviewModels.delete(path);
    const finalContents = reviewModel?.getValue() ?? (keep ? "" : original);
    const restore = preReview;
    preReview = undefined;

    // If the user navigated away during the review, leave their view alone and just drop the proposal.
    if (reviewModel === undefined || editor.getModel() !== reviewModel) {
      reviewModel?.dispose();
      return finalContents;
    }

    const fileModel = monaco.editor.getModel(fileUri);
    if (fileModel !== null) {
      // The real file is open as a working copy: show it. On keep, Claude's write → fs-change reloads it to
      // the kept content; on reject it stays at disk content. The working copy was never dirtied by the review.
      editor.setModel(fileModel);
      if (restore?.model === fileModel && restore.viewState !== null) {
        editor.restoreViewState(restore.viewState);
      }
      reviewModel.dispose();
    } else if (keep) {
      // Kept but not open as a working copy: keep showing the proposed content rather than yanking the view
      // elsewhere. Becomes a real working copy when next opened. Don't dispose — it's what's visible.
    } else if (restore !== undefined && restore.model !== null && !restore.model.isDisposed()) {
      // Rejected, no working copy for the file: restore whatever was showing before the review began.
      editor.setModel(restore.model);
      if (restore.viewState !== null) {
        editor.restoreViewState(restore.viewState);
      }
      reviewModel.dispose();
    } else {
      // Rejected and nothing else was open: clear the editor and drop the proposal.
      editor.setModel(null);
      reviewModel.dispose();
    }
    return finalContents;
  };

  const dispose = (): void => {
    // Best-effort flush of any pending edit before teardown (fire-and-forget).
    for (const key of [...saveTimers.keys()]) {
      flushSave(key);
    }
    if (emitTimer !== undefined) {
      clearTimeout(emitTimer);
    }
    // Flush the active tab's view state synchronously (the debounced timer dies with teardown) so the rebuilt
    // host's restoreSession() reopens it precisely. The tab set already lives in the store.
    if (viewStateTimer !== undefined) {
      clearTimeout(viewStateTimer);
      viewStateTimer = undefined;
    }
    snapshotViewState();
    for (const subscription of disposables) {
      subscription.dispose();
    }
    spelling.dispose();
    editor.dispose();
    // The model references are not disposed — they persist on window so the next host reattaches to the same
    // working copies and the refcount never hits 0.
  };

  // Restore the editor on every fresh widget build (relaunch, Ctrl+R, hot reload); the session store is already
  // seeded (from disk on `ready` or carried across the hot-swap). Reopens the active file via showFile,
  // re-adopting a surviving working copy rather than re-reading. Non-active entries reopen lazily.
  const restoreSession = async (): Promise<void> => {
    const session = editorSession();
    if (session === null || session.active === null) {
      return;
    }
    const entry = session.open.find((open) => open.path === session.active);
    // A web/source overlay tab has no Monaco model — never read its URL/target as a file. App renders the
    // iframe/shadow-root over the (released) editor; showFile'ing the URL would read it as a path, which on
    // Windows is a malformed read that surfaces a persistent "Unable to read file" toast rather than a swallowed
    // FileNotFound. Mirrors applyActive's web/source guard for the rebind/restore path — including its media
    // guard: a media file tab restores into the MediaPane overlay, never a text working copy.
    if (
      entry === undefined ||
      entry.kind === "web" ||
      entry.kind === "source" ||
      mediaTypeOf(entry.path) !== null
    ) {
      return;
    }
    await showFile(monaco.Uri.file(canonicalFsPath(entry.path)), {
      viewState: (entry.viewState ?? null) as monaco.editor.ICodeEditorViewState | null,
    });
  };

  await restoreSession();

  // Rebind on a session switch: release the previous session's working copies, then reopen the incoming
  // session's active tab via restoreSession. Non-active tabs reopen lazily when clicked.
  const rebindSession = async (): Promise<void> => {
    releaseAll();
    await restoreSession();
  };

  // Wait for Monaco's first paint before resolving, so the caller (which fades the splash on resolution)
  // keeps the initial layout hidden under the splash rather than flashing into view.
  await nextPaint();

  log("info", "editor host ready");
  return {
    editor,
    spellContextAtClientPoint: (clientX, clientY) =>
      spelling.contextAtClientPoint(clientX, clientY),
    spellContextAtCursor: () => spelling.contextAtCursor(),
    requestSpellSuggestions: (context) => spelling.requestSuggestions(context),
    applySpellSuggestion: (args) => spelling.applySuggestion(args),
    addSpellWord: (scope, args) => spelling.addWord(scope, args),
    handleSpellCheckResult: (message) => spelling.handleCheckResult(message),
    handleSpellSuggestResult: (message) => spelling.handleSuggestResult(message),
    handleSpellAddWordResult: (message) => spelling.handleAddWordResult(message),
    handleSpellRestoreResult: (message) => spelling.handleRestoreResult(message),
    handleSpellDictionaryChanged: (message) => spelling.handleDictionaryChanged(message),
    clearSpellProjection: () => spelling.clearProjection(),
    show,
    closeFile,
    contentOf,
    cancelSave,
    flush,
    flushDirty,
    clear,
    rebindSession,
    beginReview,
    endReview,
    dispose,
  };
}
