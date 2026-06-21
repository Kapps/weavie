// The Monaco editor and the @codingame/monaco-vscode-api service layer behind it are by far the
// heaviest code in the app. This module is *dynamically imported* (see App.tsx onMount) so all of it
// lands in a separate chunk that loads AFTER the shell (the terminal panes) has painted — keeping the
// multi-megabyte editor code off the first-paint path. Everything Monaco-touching that the shell needs
// is reached through the EditorHost handle returned here.
//
// File models are real VSCode *working copies*: opened via ITextModelService.createModelReference (which
// resolves the file through the host-backed file:// provider → real disk), saved through the same provider
// on weavie's debounce, and reloaded by VSCode's own model manager when the host pushes an fs-change. This
// is what makes every Monaco feature that resolves a file:// model reference (occurrence highlighting,
// peek/references, format, …) actually work — they reuse the one working copy per URI instead of failing to
// read an empty provider. See host-file-provider.ts and docs/specs/file-management-and-sessions.md.

import {
  ITextFileService,
  ITextModelService,
  getService,
} from "@codingame/monaco-vscode-api/services";
import { log, postToHost } from "../bridge";
import { startLanguageServices } from "../lsp/lsp-client";
import { setDirtyPath } from "./dirty-store";
import { canonicalFsPath } from "./fs-path";
import { createEditor, monaco } from "./monaco-setup";
import { captureViewState, editorSession, openTab, promote } from "./session-store";
import { initEditorServices, setOpenEditorSink } from "./vscode-services";

// A resolved, refcounted model reference held for an open file. Disposing it drops a refcount; the model is
// freed only when no reference remains (so a feature's transient createModelReference never frees our model).
type ModelRef = Awaited<ReturnType<ITextModelService["createModelReference"]>>;

// The scheme for the transient model that backs an openDiff review. NOT `file://`, so it is never a working
// copy, never resolved by the host file provider, and never pushed as the active editor (the active-editor
// notification filters scheme "file") — so a review can't dirty/collide with the real file working copy.
const REVIEW_SCHEME = "weavie-review";

// The open file *working copies* must survive a Vite hot reload. A hot reload rebuilds the editor widget (App
// is the HMR boundary — see vscode-services.ts), but the working copies live in the global VSCode model/text-
// file services. Keep their model references alive across the rebuild on `window` so the new host re-adopts
// them — the refcount never hits 0, so they aren't torn down (unsaved edits survive, no disk re-read). WHICH
// file was active + its position is NOT stashed here: dispose() flushes the live state into the (HMR-surviving)
// session store, so restoreSession() reopens it on the next build exactly as it does on relaunch. Dev-only —
// production never hot-reloads.
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
   * Switches the editor to a file working copy. `placement` either reveals a 1-based `line` (a fresh open or
   * an explicit navigation) or restores the tab's saved Monaco view state (switching back to an open tab).
   * The single path that swaps the editor to a file — a user open, a tab switch, and a restore all use it.
   */
  show(path: string, placement: { line: number } | { viewState: unknown }): void;
  /**
   * Closes a tab: flushes its pending save, then releases its refcounted working-copy reference so the model
   * can be freed. This is the ONLY site that disposes a reference, and it runs only on an explicit user close
   * — never from dispose() (a hot reload keeps the references alive on window so the rebuilt host re-adopts
   * them). The caller must switch the editor off this model first (see clear / show), then close.
   * Pass `discard` to skip the flush (a scratch buffer being discarded or converted on save — its temp file
   * is deleted host-side, so flushing it would be pointless or re-create it).
   */
  closeFile(path: string, discard?: boolean): void;
  /** The current text of an open file's working copy (for a scratch save / discard check), or undefined. */
  contentOf(path: string): string | undefined;
  /** Cancels a file's pending debounced save (so no autosave fires while a scratch save dialog is open). */
  cancelSave(path: string): void;
  /** Clears the editor to an empty pane (the last tab was closed). */
  clear(): void;
  /**
   * Rebinds the editor to the (already-updated) session store after a session switch: flushes + releases
   * every open working copy from the previous session, then reopens the new active tab from the session
   * signal (non-active tabs reopen lazily, exactly as on launch). The session store's set-editor-session
   * listener has already flipped the signal to the incoming session before this runs.
   */
  rebindSession(): Promise<void>;
  /**
   * Begins an inline review of an openDiff proposal in a transient model (the real file working copy is left
   * untouched), makes it the active editor showing `proposed` revealed at `line` (1-based — the proposal's
   * first changed hunk), and returns the transient model's URI string so the caller can render the inline diff
   * over it.
   */
  beginReview(path: string, proposed: string, line: number): string;
  /**
   * Ends an inline review and returns the proposal's final (possibly tweaked) content. Restores the editor off
   * the transient review model: to the file's working copy when it's open (it reloads to the kept content via
   * the host's fs-change on accept, or stays at disk content on reject); when the file isn't open, a kept
   * proposal keeps showing the proposed content (it becomes a working copy on reopen) and a rejected one
   * returns to the prior view. Disposes the transient review model.
   */
  endReview(path: string, keep: boolean, original: string): string;
  /**
   * Tears the host down: flushes any pending save, drops all subscriptions (including those on models that
   * outlive the widget), and disposes the editor. The session's file working copies and their references are
   * intentionally NOT released — they persist on window so the next host (e.g. after a hot reload) reattaches.
   */
  dispose(): void;
}

/** A real user file worth saving / reporting as active: a `file://` model (the editor's working copies). */
function isUserFileModel(model: monaco.editor.ITextModel): boolean {
  return model.uri.scheme === "file";
}

/**
 * Brings up the editor: initializes the VSCode services (which must precede any editor creation),
 * creates the editor in `container`, and wires lazy per-language LSP. The caller (App) catches failures
 * so a broken editor never takes down the terminal panes. `onSaveError` surfaces a debounced save that
 * failed to reach disk as a user-facing toast (never a silent drop).
 */
export async function createEditorHost(
  container: HTMLElement,
  onSaveError?: (message: string) => void,
): Promise<EditorHost> {
  await initEditorServices();
  const textModelService = await getService(ITextModelService);
  const textFileService = await getService(ITextFileService);
  const editor = createEditor(container);

  // Open file working copies survive a hot reload on this window-scoped map; first host creates it.
  window.__WEAVIE_EDITOR_REFS__ ??= new Map<string, ModelRef>();
  const refs = window.__WEAVIE_EDITOR_REFS__;

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
  // it). Debounced — cursor moves fire rapidly and Claude only needs the settled state. The transient
  // review model (scheme weavie-review) is suppressed; it isn't a file the user is working on.
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
  // models), so a rebuilt host never stacks a second set of save/selection handlers on a surviving model.
  const disposables: monaco.IDisposable[] = [
    editor.onDidChangeModel(scheduleEmitActiveEditor),
    editor.onDidChangeCursorSelection(scheduleEmitActiveEditor),
  ];

  // Mirror each file working copy's dirty state into the (HMR-surviving) dirty store so the tab strip can show
  // an unsaved-changes `*`. With autosave the dirty window is brief — a debounced flush clears it — but the
  // error gate below can hold a flush back, so the marker is the user's signal that an edit hasn't reached
  // disk yet. Seed from the models already in memory first (covers a hot reload re-adopting working copies),
  // then track changes; the listener joins `disposables` so a rebuilt host doesn't stack a second one.
  for (const model of textFileService.files.models) {
    setDirtyPath(model.resource.fsPath, model.isDirty());
  }
  disposables.push(
    textFileService.files.onDidChangeDirty((model) => {
      setDirtyPath(model.resource.fsPath, model.isDirty());
    }),
  );

  // Keep the active tab's Monaco view state (scroll/cursor/folding) fresh in the session store so a relaunch —
  // and a hot reload — reopens it at the same position. Data-only: this updates the active entry IN PLACE via
  // captureViewState and never changes which tab is active or their order (that flows the other way, store →
  // host, through show()), so there is no captureViewState↔show loop. Only a real file working copy is
  // captured (isUserFileModel), so an empty editor or a transient review model is ignored. Debounced like the
  // active-editor emit; scroll feeds it too (view state includes scroll). The listeners join `disposables` so a
  // hot-reloaded host doesn't stack a second set on the surviving models.
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

  // Save: the working copy is debounce-flushed to disk through the file provider so the embedded Claude
  // (which reads disk directly) sees the user's current state. A blind overwrite (ignoreModifiedSince) —
  // weavie's buffer is authoritative, matching the live-refresh-all-modes policy; there is no save-conflict
  // dialog. A reload/revert updates the model WITHOUT marking it dirty, so the isDirty guard means those
  // never schedule a (no-op) save.
  const saveAttached = new WeakSet<monaco.editor.ITextModel>();
  const saveTimers = new Map<string, ReturnType<typeof setTimeout>>();

  // Error gate (no-errors → errors): we don't want to push a buffer to disk the instant an edit makes it
  // syntactically broken, because the embedded Claude reads disk directly. So a flush is HELD while the file
  // currently shows error markers AND its last saved state was clean — but only up to ERROR_HOLD_MS, after
  // which it saves anyway (we never let disk diverge from the working copy indefinitely). A file that was
  // ALREADY erroring keeps saving normally: withholding can't un-break what's already on disk, and the held
  // edit is released the moment the errors clear (onDidChangeMarkers below). This is best-effort: LSP
  // diagnostics lag the keystroke, so a flush firing before they catch up can still persist a just-broken
  // edit. It reliably catches the case that matters — pausing in a broken state long enough to be flagged.
  const ERROR_HOLD_MS = 1500;
  // Whether the last flush we performed for a key persisted erroring content (default clean) — drives the
  // no-errors → errors transition test. And, per held key, when we first started withholding its flush.
  const savedHadErrors = new Map<string, boolean>();
  const holdingSince = new Map<string, number>();

  const hasErrors = (uri: monaco.Uri): boolean =>
    monaco.editor
      .getModelMarkers({ resource: uri })
      .some((marker) => marker.severity === monaco.MarkerSeverity.Error);

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
      // Clean → erroring: hold the flush, bounded by ERROR_HOLD_MS. onDidChangeMarkers retries the moment the
      // errors clear; this timer is the fallback that saves anyway if they don't.
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
      .catch((error: unknown) => {
        const name = key.split("/").pop() ?? key;
        const message = `Couldn't save ${name}: ${String(error)}`;
        log("error", message);
        onSaveError?.(message);
      });
  };

  // Release a held flush as soon as a file's errors clear (rather than waiting for the next keystroke or the
  // ERROR_HOLD_MS fallback). Only touches files we're actively holding, so it's cheap on every marker update.
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
        // Only a real user edit dirties the working copy; a host-driven reload/revert does not (VSCode sets
        // ignoreDirtyOnModelContentChange during reload), so this skips scheduling a redundant save for it.
        if (!textFileService.isDirty(model.uri)) {
          return;
        }
        // A real edit promotes a preview tab to persistent (no-op once persistent — runs only on the first edit).
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

  // Resolve (or reuse) the refcounted working-copy reference for a file URI, and wire its save listener once.
  const ensureRef = async (uri: monaco.Uri): Promise<ModelRef> => {
    const key = uri.toString();
    let ref = refs.get(key);
    if (ref === undefined) {
      ref = await textModelService.createModelReference(uri);
      refs.set(key, ref);
    }
    attachSave(ref.object.textEditorModel);
    return ref;
  };

  // THE single path that swaps the editor to a file working copy — a host/user open AND a session /
  // hot-reload restore all go through here, so a restored file is opened *identically* to one the user
  // clicked. `placement` is the only difference: reveal a `line` (open) or restore a saved Monaco view
  // state (restore). Crucially, setting the model fires onDidChangeModel, which is what drives the
  // active-editor notification to Claude (active-editor-changed) and App's currentFile tracking — so that
  // happens by construction on every open, with no path needing to re-send it. Opens can arrive faster than
  // they resolve (createModelReference is async); openSeq makes the latest win, so a slow resolve can't
  // clobber a newer open — and a user's open during launch supersedes the in-flight restore.
  let openSeq = 0;
  const showFile = async (
    uri: monaco.Uri,
    placement:
      | { line: number }
      | { selection: monaco.IRange }
      | { viewState: monaco.editor.ICodeEditorViewState | null },
  ): Promise<void> => {
    // Snapshot the outgoing tab's position before we swap away (data-only store write; never loops back).
    snapshotViewState();
    const token = ++openSeq;
    try {
      const ref = await ensureRef(uri);
      if (token !== openSeq) {
        return; // superseded by a newer open
      }
      editor.setModel(ref.object.textEditorModel);
      if ("line" in placement) {
        editor.revealLineInCenter(placement.line);
        editor.setPosition({ lineNumber: placement.line, column: 1 });
        editor.focus();
      } else if ("selection" in placement) {
        editor.setSelection(placement.selection);
        editor.revealRangeInCenterIfOutsideViewport(placement.selection);
        editor.focus();
      } else if (placement.viewState !== null) {
        editor.restoreViewState(placement.viewState);
      }
    } catch (error) {
      log("error", `open failed for ${uri.toString()}: ${String(error)}`);
    }
  };

  const show = (path: string, placement: { line: number } | { viewState: unknown }): void => {
    const resolved =
      "line" in placement
        ? placement
        : { viewState: placement.viewState as monaco.editor.ICodeEditorViewState | null };
    void showFile(monaco.Uri.file(canonicalFsPath(path)), resolved);
  };

  // Close a tab: flush any pending save, then release the refcounted working-copy reference so the model can be
  // freed. The ONLY site that disposes a reference, and it runs only on an explicit user close. dispose() must
  // NEVER call this — a hot reload rebuilds the widget but keeps the working copies alive on window so the new
  // host re-adopts them; disposing here would defeat that. The caller switches the editor off this model first.
  const closeFile = (path: string, discard = false): void => {
    const key = monaco.Uri.file(canonicalFsPath(path)).toString();
    if (discard) {
      // A discarded/converted scratch buffer: drop any pending save instead of flushing — its temp file is
      // being deleted host-side, so a flush would either be wasted or re-create the file we're discarding.
      const timer = saveTimers.get(key);
      if (timer !== undefined) {
        clearTimeout(timer);
        saveTimers.delete(key);
      }
      holdingSince.delete(key);
    } else {
      flushSave(key);
    }
    const ref = refs.get(key);
    if (ref !== undefined) {
      ref.dispose();
      refs.delete(key);
    }
  };

  // The current text of an open working copy (used to seed a scratch "save as" and to decide whether a
  // scratch close needs a discard confirm). Undefined when the file isn't open as a working copy.
  const contentOf = (path: string): string | undefined => {
    const key = monaco.Uri.file(canonicalFsPath(path)).toString();
    return refs.get(key)?.object.textEditorModel.getValue();
  };

  // Cancel a file's pending debounced save. Called just before opening the native scratch save dialog so an
  // in-flight autosave can't re-create the temp file after the host has saved + deleted it.
  const cancelSave = (path: string): void => {
    const key = monaco.Uri.file(canonicalFsPath(path)).toString();
    const timer = saveTimers.get(key);
    if (timer !== undefined) {
      clearTimeout(timer);
      saveTimers.delete(key);
    }
    holdingSince.delete(key);
  };

  const clear = (): void => {
    editor.setModel(null);
  };

  // Release EVERY open working copy: flush each real file's pending save (a scratch buffer flushes to its
  // temp file — not discarded), drop its refcounted reference so the model can be freed, then empty the
  // editor. Used by rebindSession on a session switch to let go of the previous session's worktree files
  // before the incoming session's are opened. Unlike dispose() this DOES release the refs — a switch is not
  // a hot reload, so the previous session's models must actually be torn down.
  const releaseAll = (): void => {
    for (const [key, ref] of [...refs]) {
      flushSave(key);
      ref.dispose();
      refs.delete(key);
    }
    editor.setModel(null);
  };

  // Route the editor service's file-opens (go-to-def / peek / references) through the tab store as a PREVIEW
  // open, then reveal the target range — so navigating reuses the one preview slot instead of piling up tabs.
  // Re-registered on every host build (it closes over this editor), like the active editor, so it survives HMR.
  setOpenEditorSink((uri, selection) => {
    openTab(uri.fsPath, { preview: true });
    void showFile(uri, selection !== undefined ? { selection } : { line: 1 });
  });

  // Review uses a transient model per file path (one openDiff is live at a time — it blocks). Tracked so
  // endReview can read its final content and dispose it.
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
    // A non-file URI whose path still ends in the real filename, so Monaco infers the language (syntax
    // highlighting) from the extension while the scheme keeps it out of the file-service / working-copy world.
    const reviewUri = monaco.Uri.from({ scheme: REVIEW_SCHEME, path: fileUri.path });
    let model = monaco.editor.getModel(reviewUri);
    if (model === null) {
      model = monaco.editor.createModel(proposed, undefined, reviewUri);
    } else {
      model.setValue(proposed);
    }
    reviewModels.set(path, model);
    // Taking over the editor synchronously: invalidate any in-flight async open so its late setModel can't
    // clobber this review model. This matters when the host re-renders a held diff right after a session switch
    // — the rebind's restoreSession (an async showFile) is still resolving when this runs.
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

    // If the user navigated away during the review, leave their current view alone — just drop the proposal.
    if (reviewModel === undefined || editor.getModel() !== reviewModel) {
      reviewModel?.dispose();
      return finalContents;
    }

    const fileModel = monaco.editor.getModel(fileUri);
    if (fileModel !== null) {
      // The real file is open as a working copy: show it. On keep, Claude's write → fs-change reloads it to
      // the kept content; on reject it stays at disk content. The file working copy was never dirtied by the
      // review, so no save/conflict fires.
      editor.setModel(fileModel);
      if (restore?.model === fileModel && restore.viewState !== null) {
        editor.restoreViewState(restore.viewState);
      }
      reviewModel.dispose();
    } else if (keep) {
      // Kept, but the file isn't open as a working copy (a brand-new file, or an existing one that wasn't
      // open): keep showing the proposed content rather than snapping back to whatever was here before —
      // keeping an edit shouldn't yank the view to a different file. It becomes a real working copy when the
      // file is next opened, after Claude's write lands. Don't dispose — it's what's visible.
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
    // Best-effort flush of any pending edit before teardown (fire-and-forget; on a hot reload the working
    // copy survives anyway, on a real unload the browser is going away).
    for (const key of [...saveTimers.keys()]) {
      flushSave(key);
    }
    if (emitTimer !== undefined) {
      clearTimeout(emitTimer);
    }
    // Flush the active tab's view state SYNCHRONOUSLY (the debounced timer would be dropped by the teardown).
    // On a hot reload this lands the exact position in the HMR-surviving session store, so the rebuilt host's
    // restoreSession() reopens it precisely — no separate snapshot path needed. The tab SET itself already
    // lives in the store (mutated as tabs open/close), so only the active position needs flushing here.
    if (viewStateTimer !== undefined) {
      clearTimeout(viewStateTimer);
      viewStateTimer = undefined;
    }
    snapshotViewState();
    for (const subscription of disposables) {
      subscription.dispose();
    }
    editor.dispose();
    // The model references are intentionally NOT disposed — they persist on window so the next host
    // (after a hot reload) reattaches to the same working copies and the refcount never hits 0.
  };

  // Restore the editor on every fresh build of the widget — relaunch, Ctrl+R, AND hot reload. The session
  // store is seeded from disk by the host's set-editor-session push on `ready` (relaunch / Ctrl+R) or carried
  // live across the hot-swap (HMR; dispose() flushed the exact state into it), so the same read covers all
  // three. Reopen the active file through the SAME open path as a user open (showFile) — disk is read through
  // the host file provider (no content on the wire), Claude is told the active editor, and a surviving working
  // copy is re-adopted via ensureRef rather than re-read — at its saved view state. Non-active entries reopen
  // lazily (no eager LSP spin-up for invisible files).
  const restoreSession = async (): Promise<void> => {
    const session = editorSession();
    if (session === null || session.active === null) {
      return;
    }
    const entry = session.open.find((open) => open.path === session.active);
    if (entry === undefined) {
      return;
    }
    await showFile(monaco.Uri.file(canonicalFsPath(entry.path)), {
      viewState: (entry.viewState ?? null) as monaco.editor.ICodeEditorViewState | null,
    });
  };

  await restoreSession();

  // Rebind to a different session on a switch: let go of the previous session's working copies, then reopen
  // the incoming session's active tab from the (already-updated) session signal — reusing restoreSession so a
  // switched-in file opens identically to a launch restore. Non-active tabs reopen lazily when clicked.
  const rebindSession = async (): Promise<void> => {
    releaseAll();
    await restoreSession();
  };

  // Wait for Monaco's first real paint before resolving. The caller fades the splash on resolution, so
  // this keeps the editor's initial layout/paint hidden under the splash rather than flashing into view.
  await nextPaint();

  log("info", "editor host ready");
  return {
    editor,
    show,
    closeFile,
    contentOf,
    cancelSave,
    clear,
    rebindSession,
    beginReview,
    endReview,
    dispose,
  };
}
