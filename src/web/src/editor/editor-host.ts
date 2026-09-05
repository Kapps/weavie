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
import { type ClientSession, log, selectedSession } from "../bridge";
import { noteSelectionChange, registerSelectionSource } from "../commands/selection";
import { startLanguageServices } from "../lsp/lsp-client";
import { installReferenceCommands } from "../lsp/reference-commands";
import { installTestLenses } from "../tests/test-lens";
import { activeEditorMessage } from "./active-editor-message";
import { installAltClickPeek } from "./alt-click-peek";
import { setDirtyPath } from "./dirty-store";
import { setEditorStatus } from "./editor-status-store";
import { mediaTypeOf } from "./media/media-types";
import { createEditor, monaco } from "./monaco-setup";
import { leaveLine } from "./nav-history";
import { REVEAL_SCROLL } from "./reveal-scroll";
import { captureViewStateFor, editorSessionFor, type Placement, promoteFor } from "./session-store";
import {
  SESSION_FILE_SCHEME,
  sessionFileUri,
  sessionForUri,
  sessionOwnsUri,
  sessionUriHostPath,
} from "./session-uri";
import { initEditorServices, setOpenEditorSink } from "./vscode-services";

// A resolved, refcounted model reference held for an open file. Disposing it drops a refcount; the model is
// freed only when no reference remains, so a feature's transient createModelReference never frees ours.
type ModelRef = Awaited<ReturnType<ITextModelService["createModelReference"]>>;

/** One unified-review model: a live working copy when present, otherwise a read-only deleted snapshot. */
export interface ReviewCopy {
  model: monaco.editor.ITextModel;
  editable: boolean;
}

/** Owns every model reference acquired by one mounted unified-review surface. */
export interface ReviewCopyScope {
  open(
    session: ClientSession,
    path: string,
    current: string,
    currentExists: boolean,
  ): Promise<ReviewCopy>;
  dispose(): void;
}

// Scheme for the transient openDiff review model. Not `file://`, so it's never a working copy, never resolved
// by the host file provider, and never the active editor — a review can't dirty or collide with the real file.
const REVIEW_SCHEME = "weavie-review";

// Open working copies belong to their tabs across session switches. Keeping their references on `window` also
// lets a Vite hot reload rebuild the widget without dropping unsaved edits or rereading from disk.
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
  /**
   * Switches the editor to a file working copy; `placement` reveals a line/selection or restores the tab's saved
   * view state. Resolves `true` when shown (or superseded), `false` when unreadable so the caller can roll back.
   */
  show(session: ClientSession, path: string, placement: Placement): Promise<boolean>;
  /**
   * Closes a tab: flushes its pending save, then releases its refcounted working-copy reference (the only site
   * that disposes one, never dispose()). The caller must switch the editor off this model first. Pass `discard`
   * to skip the flush (a scratch buffer being discarded/converted, whose temp file is deleted host-side).
   */
  closeFile(session: ClientSession, path: string, discard?: boolean): void;
  /** The current text of an open file's working copy (for a scratch save / discard check), or undefined. */
  contentOf(session: ClientSession, path: string): string | undefined;
  /** Cancels a file's pending debounced save (so no autosave fires while a scratch save dialog is open). */
  cancelSave(session: ClientSession, path: string): void;
  /**
   * Flushes a file's pending debounced save and resolves once it lands, so a host action that reads the file
   * next (a per-hunk revert's guard) sees current content. No-op when not dirty.
   */
  flush(session: ClientSession, path: string): Promise<void>;
  /**
   * Flushes every dirty working copy to its editor-owning backend and resolves once all saves land. Called
   * before a cross-backend switch so unsaved edits persist on their own host.
   */
  flushDirty(): Promise<void>;
  /** Flushes dirty working copies belonging to one exact session before that backend is torn down. */
  flushSession(session: ClientSession): Promise<void>;
  /** Creates one model-reference scope owned by a mounted unified-review surface. */
  createReviewCopyScope(): ReviewCopyScope;
  /**
   * The code editor holding keyboard focus — a unified-review section's, else the main pane. Menu-triggered
   * actions (Copy/Cut/Paste) must act on what the user is actually typing in.
   */
  focusedEditor(): monaco.editor.ICodeEditor;
  /** Clears the editor to an empty pane (the last tab was closed). */
  clear(): void;
  /**
   * Rebinds the editor to the (already-updated) session store after a switch: releases the previous session's
   * review copies, then reuses the new active tab's warm working copy (non-active tabs reopen lazily).
   */
  rebindSession(session: ClientSession): Promise<void>;
  /** Releases working copies no longer owned by one of the session's open file tabs. */
  reconcileSession(session: ClientSession, openPaths: readonly string[]): void;
  /**
   * Begins an inline review of an openDiff proposal in a transient model (the working copy is left untouched),
   * shows `proposed` revealed at 1-based `line`, and returns the model's URI so the caller renders the diff over it.
   */
  beginReview(session: ClientSession, path: string, proposed: string, line: number): string;
  /**
   * Ends an inline review and returns the proposal's final content. Restores the editor off the review model:
   * to the file's working copy when open, else a kept proposal keeps showing, a rejected one returns to the
   * prior view. Disposes the review model.
   */
  endReview(session: ClientSession, path: string, keep: boolean, original: string): string;
  /**
   * Tears the host down: flushes pending saves, drops all subscriptions (including on models that outlive the
   * widget), disposes the editor. Working copies and references persist on window so the next host reattaches.
   */
  dispose(): void;
}

/** A real user file worth saving / reporting as active: a `file://` model (the editor's working copies). */
function isUserFileModel(model: monaco.editor.ITextModel): boolean {
  return model.uri.scheme === SESSION_FILE_SCHEME && sessionForUri(model.uri) !== undefined;
}

/**
 * Brings up the editor: initializes the VSCode services (must precede editor creation), creates the editor in
 * `container`, wires lazy per-language LSP. `onSaveError` / `onOpenError` surface a failed save / open as a
 * toast so neither strands silently. The callbacks return viewport history and Monaco-initiated destinations to
 * the controller, which owns navigation and tab state.
 */
export async function createEditorHost(
  container: HTMLElement,
  onSaveError: (message: string) => void,
  onOpenError: (message: string) => void,
  onLeaveViewport: (loc: { session: ClientSession; path: string; line: number }) => void,
  onOpenEditor: (destination: {
    session: ClientSession;
    path: string;
    selection: monaco.IRange | undefined;
  }) => void,
): Promise<EditorHost> {
  await initEditorServices();
  const textModelService = await getService(ITextModelService);
  const textFileService = await getService(ITextFileService);
  const editor = createEditor(container);

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
  const emitActiveEditor = (): void => {
    const model = editor.getModel();
    if (model === null || !isUserFileModel(model)) {
      return;
    }
    const session = sessionForUri(model.uri);
    if (session === undefined) {
      return;
    }
    const sel = editor.getSelection();
    session.feature("editor").publish("activeChanged", activeEditorMessage(model, sel));
  };
  const scheduleEmitActiveEditor = (): void => {
    if (emitTimer !== undefined) {
      clearTimeout(emitTimer);
    }
    emitTimer = setTimeout(emitActiveEditor, 150);
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
      container.dataset.activeFile = sessionUriHostPath(model.uri);
    } else {
      delete container.dataset.activeFile;
    }
  };

  // The editor's selected text as a search-seed source — Monaco keeps its selection out of the DOM, so the
  // document tracker never sees it.
  const readSelection = (): string => {
    const model = editor.getModel();
    const selection = editor.getSelection();
    return model === null || selection === null || selection.isEmpty()
      ? ""
      : model.getValueInRange(selection);
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
    { dispose: registerSelectionSource("editor", readSelection) },
    editor.onDidChangeCursorSelection(() => noteSelectionChange("editor")),
    installAltClickPeek(editor),
  ];

  // Mirror each working copy's dirty state into the dirty store so the tab strip shows an unsaved `*` (the error
  // gate below can hold a flush back). Seed from in-memory models (covers a hot reload), then track changes.
  for (const model of textFileService.files.models) {
    const session = sessionForUri(model.resource);
    if (session !== undefined) {
      setDirtyPath(session, sessionUriHostPath(model.resource), model.isDirty());
    }
  }
  disposables.push(
    textFileService.files.onDidChangeDirty((model) => {
      const session = sessionForUri(model.resource);
      if (session !== undefined) {
        setDirtyPath(session, sessionUriHostPath(model.resource), model.isDirty());
      }
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
    const session = sessionForUri(model.uri);
    if (session !== undefined) {
      captureViewStateFor(session, sessionUriHostPath(model.uri), editor.saveViewState() ?? null);
    }
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
    onSaveError(message);
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
    if (saveAttached.has(model) || !isUserFileModel(model)) {
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
        const session = sessionForUri(model.uri);
        if (session !== undefined) {
          promoteFor(session, sessionUriHostPath(model.uri));
        }
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
    const owner = sessionForUri(uri);
    if (owner === undefined || selectedSession() !== owner) {
      return true;
    }
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
      leaving !== null &&
      cursor !== null &&
      top !== undefined &&
      bottom !== undefined &&
      isUserFileModel(leaving) &&
      leaving.uri.toString() !== uri.toString()
    ) {
      const line = leaveLine(cursor.lineNumber, top.startLineNumber, bottom.endLineNumber);
      if (line !== undefined) {
        const session = sessionForUri(leaving.uri);
        if (session !== undefined) {
          onLeaveViewport({
            session,
            path: sessionUriHostPath(leaving.uri),
            line,
          });
        }
      }
    }
    const token = ++openSeq;
    try {
      const resolved = await resolveRef(uri);
      let ref = resolved.ref;
      if (token !== openSeq || selectedSession() !== owner) {
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
        editor.revealPositionInCenter(position, REVEAL_SCROLL);
        editor.setPosition(position);
        // focus: false = reveal only (the search panel's live preview keeps typing in its own input).
        if (placement.focus !== false) {
          editor.focus();
        }
      } else if ("selection" in placement) {
        editor.setSelection(placement.selection);
        editor.revealRangeInCenterIfOutsideViewport(placement.selection, REVEAL_SCROLL);
        editor.focus();
      } else if (placement.viewState !== null) {
        editor.restoreViewState(placement.viewState);
      }
      return true;
    } catch (error) {
      // A genuine read failure. If a newer open superseded this one, stay quiet — it owns the editor. Otherwise
      // error loudly: the model never swapped, so without this the tab would sit blank with no signal.
      if (token !== openSeq) {
        return true;
      }
      log("error", `open failed for ${uri.toString()}: ${String(error)}`);
      const name = uri.path.split("/").pop() ?? uri.path;
      onOpenError(`Couldn't open ${name}: ${String(error)}`);
      return false;
    }
  };

  const show = (session: ClientSession, path: string, placement: Placement): Promise<boolean> => {
    const resolved =
      "line" in placement || "selection" in placement
        ? placement
        : { viewState: placement.viewState as monaco.editor.ICodeEditorViewState | null };
    return showFile(sessionFileUri(session, path), resolved);
  };

  // Close a tab: flush any pending save, then release the refcounted reference (only site that disposes one;
  // never dispose(), since a hot reload keeps copies alive on window). Caller switches the editor off first.
  const closeFile = (session: ClientSession, path: string, discard = false): void => {
    const key = sessionFileUri(session, path).toString();
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
    setDirtyPath(session, path, false);
  };

  // The current text of an open working copy (seeds a scratch "save as" and decides whether a scratch close
  // needs a discard confirm). Undefined when the file isn't open as a working copy.
  const contentOf = (session: ClientSession, path: string): string | undefined => {
    const key = sessionFileUri(session, path).toString();
    return refs.get(key)?.object.textEditorModel.getValue();
  };

  // Flush a file's pending save and await it landing on disk. Used before a per-hunk revert so the host's
  // optimistic-concurrency guard reads current content, not a version the debounce hasn't written. No-op when clean.
  const flush = async (session: ClientSession, path: string): Promise<void> => {
    const key = sessionFileUri(session, path).toString();
    cancelPendingSave(key);
    const uri = monaco.Uri.parse(key);
    if (!textFileService.isDirty(uri)) {
      return;
    }
    await textFileService.save(uri, { ignoreModifiedSince: true, ignoreErrorHandler: true });
  };

  // Cancel a file's pending debounced save. Called before opening the native scratch save dialog so an
  // in-flight autosave can't re-create the temp file after the host has saved + deleted it.
  const cancelSave = (session: ClientSession, path: string): void => {
    cancelPendingSave(sessionFileUri(session, path).toString());
  };

  // Each mounted unified review owns an exact scope. An old surface's late cleanup can therefore release only
  // its own references, never the copies a rapidly-mounted successor just acquired.
  const reviewScopes = new Set<() => void>();
  let reviewScopeSequence = 0;
  const createReviewCopyScope = (): ReviewCopyScope => {
    const refsByUri = new Map<string, ModelRef>();
    const snapshots = new Map<string, monaco.editor.ITextModel>();
    const scope = ++reviewScopeSequence;
    let disposed = false;
    const dispose = (): void => {
      if (disposed) {
        return;
      }
      disposed = true;
      reviewScopes.delete(dispose);
      for (const [key, ref] of refsByUri) {
        flushSave(key);
        ref.dispose();
      }
      refsByUri.clear();
      for (const model of snapshots.values()) {
        model.dispose();
      }
      snapshots.clear();
    };
    reviewScopes.add(dispose);

    return {
      open: async (session, path, current, currentExists) => {
        if (disposed) {
          throw new Error("the review closed while this file was loading");
        }
        const fileUri = sessionFileUri(session, path);
        const key = fileUri.toString();
        if (!currentExists) {
          const held = snapshots.get(key);
          if (held !== undefined) {
            return { model: held, editable: false };
          }
          const uri = fileUri.with({ scheme: REVIEW_SCHEME, query: `deleted=${scope}` });
          const model = monaco.editor.createModel(current, undefined, uri);
          if (disposed) {
            model.dispose();
            throw new Error("the review closed while this file was loading");
          }
          snapshots.set(key, model);
          return { model, editable: false };
        }

        const held = refsByUri.get(key);
        if (held !== undefined) {
          return { model: held.object.textEditorModel, editable: true };
        }
        const ref = await textModelService.createModelReference(fileUri);
        if (disposed) {
          ref.dispose();
          throw new Error("the review closed while this file was loading");
        }
        const existing = refsByUri.get(key);
        if (existing !== undefined) {
          ref.dispose();
          return { model: existing.object.textEditorModel, editable: true };
        }
        refsByUri.set(key, ref);
        attachSave(ref.object.textEditorModel);
        return { model: ref.object.textEditorModel, editable: true };
      },
      dispose,
    };
  };

  const disposeReviewScopes = (): void => {
    for (const dispose of [...reviewScopes]) {
      dispose();
    }
  };

  const focusedEditor = (): monaco.editor.ICodeEditor =>
    monaco.editor.getEditors().find((candidate) => candidate.hasTextFocus()) ?? editor;

  const clear = (): void => {
    snapshotViewState();
    openSeq += 1;
    editor.setModel(null);
  };

  // Park the shared widget between session projections. Open tab references stay owned by their sessions, so a
  // warm switch reuses the working copy without another host read.
  const parkSession = (): void => {
    // A rebind to media/web/source/empty opens no successor model, so it must invalidate an older async text
    // open explicitly. The immutable binding check above also keeps its eventual reference out of this session.
    openSeq += 1;
    editor.setModel(null);
  };

  const reconcileSession = (session: ClientSession, openPaths: readonly string[]): void => {
    const retained = new Set(openPaths.map((path) => sessionFileUri(session, path).toString()));
    const activeModel = editor.getModel();
    if (
      activeModel !== null &&
      activeModel.uri.scheme === SESSION_FILE_SCHEME &&
      sessionOwnsUri(session, activeModel.uri) &&
      !retained.has(activeModel.uri.toString())
    ) {
      clear();
    }
    for (const key of [...refs.keys()]) {
      const uri = monaco.Uri.parse(key);
      if (sessionOwnsUri(session, uri) && !retained.has(key)) {
        closeFile(session, sessionUriHostPath(uri));
      }
    }
  };

  // Flush matching dirty working copies and resolve only when every write lands. A failure is surfaced and
  // propagated so callers cannot release a model or tear down its backend after losing an edit.
  const flushDirtyFor = async (owner: ClientSession | null): Promise<void> => {
    const saves: Promise<void>[] = [];
    // Review scopes hold independent references, so enumerate the authoritative working-copy service rather
    // than just the tab reference map.
    for (const model of textFileService.files.models) {
      const uri = model.resource;
      const key = uri.toString();
      if (owner !== null && sessionForUri(uri) !== owner) {
        continue;
      }
      cancelPendingSave(key);
      if (!textFileService.isDirty(uri)) {
        continue;
      }
      saves.push(
        textFileService.save(uri, { ignoreModifiedSince: true, ignoreErrorHandler: true }).then(
          () => undefined,
          (error: unknown) => {
            reportSaveError(key, error);
            throw error;
          },
        ),
      );
    }
    await Promise.all(saves);
  };

  const flushDirty = (): Promise<void> => flushDirtyFor(null);

  // Hand editor-service file opens (go-to-def / references) to the controller, which owns tab activation and
  // the selected-file notification. The host only translates the session URI back to its owning destination.
  setOpenEditorSink((uri, selection) => {
    // Only real files are working copies. The transient `weavie-review:` model has no file provider, so an
    // editor-service open of one (e.g. go-to-def while a review shows) is a no-op: it's already on screen via
    // beginReview and must never become a tab or working copy.
    if (uri.scheme !== SESSION_FILE_SCHEME) {
      return;
    }
    // uriHostPath, not fsPath: the tab path is persisted host-side, so it must be host-native, not client-OS.
    const session = sessionForUri(uri);
    if (session === undefined) {
      return;
    }
    onOpenEditor({ session, path: sessionUriHostPath(uri), selection });
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

  const beginReview = (
    session: ClientSession,
    path: string,
    proposed: string,
    line: number,
  ): string => {
    const fileUri = sessionFileUri(session, path);
    // A non-file URI whose path keeps the real filename, so Monaco infers the language from the extension
    // while the scheme keeps it out of the file-service / working-copy world.
    const reviewUri = monaco.Uri.from({
      scheme: REVIEW_SCHEME,
      path: fileUri.path,
      fragment: fileUri.fragment,
    });
    let model = monaco.editor.getModel(reviewUri);
    if (model === null) {
      model = monaco.editor.createModel(proposed, undefined, reviewUri);
    } else {
      model.setValue(proposed);
    }
    reviewModels.set(fileUri.toString(), model);
    // Invalidate any in-flight async open so its late setModel can't clobber this review model — matters when
    // the host re-renders a held diff right after a session switch while restoreSession is still resolving.
    openSeq += 1;
    preReview = { model: editor.getModel(), viewState: editor.saveViewState() };
    editor.setModel(model);
    editor.revealLineInCenter(Math.max(1, line), REVEAL_SCROLL);
    editor.focus();
    return reviewUri.toString();
  };

  const endReview = (
    session: ClientSession,
    path: string,
    keep: boolean,
    original: string,
  ): string => {
    const fileUri = sessionFileUri(session, path);
    const key = fileUri.toString();
    const reviewModel = reviewModels.get(key);
    reviewModels.delete(key);
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
    disposeReviewScopes();
    editor.dispose();
    // The tab model references are not disposed — they persist on window so the next host reattaches to the same
    // working copies and the refcount never hits 0.
  };

  // Restore the editor on every fresh widget build (relaunch, Ctrl+R, hot reload); the session store is already
  // seeded (from disk on `ready` or carried across the hot-swap). Reopens the active file via showFile,
  // re-adopting a surviving working copy rather than re-reading. Non-active entries reopen lazily.
  const restoreSession = async (owner: ClientSession): Promise<void> => {
    const session = editorSessionFor(owner);
    if (session === null || session.active === null) {
      return;
    }
    const entry = session.open.find((open) => open.path === session.active);
    // An overlay tab has no Monaco model — never read its target as a file. App renders it over the released editor.
    // A media file likewise restores in MediaPane rather than as a text working copy.
    if (
      entry === undefined ||
      entry.kind === "web" ||
      entry.kind === "source" ||
      entry.kind === "plan" ||
      mediaTypeOf(entry.path) !== null
    ) {
      return;
    }
    await showFile(sessionFileUri(owner, entry.path), {
      viewState: (entry.viewState ?? null) as monaco.editor.ICodeEditorViewState | null,
    });
  };

  const rebindSession = async (session: ClientSession): Promise<void> => {
    parkSession();
    await restoreSession(session);
  };

  // Wait for Monaco's first paint before resolving, so the caller (which fades the splash on resolution)
  // keeps the initial layout hidden under the splash rather than flashing into view.
  await nextPaint();

  log("info", "editor host ready");
  return {
    editor,
    show,
    closeFile,
    reconcileSession,
    contentOf,
    cancelSave,
    flush,
    flushDirty,
    flushSession: (session) => flushDirtyFor(session),
    createReviewCopyScope,
    focusedEditor,
    clear,
    rebindSession,
    beginReview,
    endReview,
    dispose,
  };
}
