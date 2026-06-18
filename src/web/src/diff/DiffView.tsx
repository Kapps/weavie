import { type JSX, createEffect, onCleanup, onMount } from "solid-js";
import { monaco } from "../editor/monaco-setup";
import { initEditorServices } from "../editor/vscode-services";
import { currentFonts, onFontsChanged } from "../fonts";

export interface ActiveDiff {
  id: string;
  path: string;
  tabName: string;
  original: string;
  proposed: string;
}

// Renders an inbound openDiff as an editable Monaco diff over the editor pane: the current file vs the
// proposed contents (editable). Defaults to an INLINE diff (one column, changes shown in place) so a review
// reads like your editor with the change applied; `sideBySide` flips it to the two-column view. The user can
// tweak the proposal before keeping. Keep -> the host saves the (edited) modified side and replies
// FILE_SAVED to Claude; Reject -> DIFF_REJECTED.
export function DiffView(props: {
  diff: ActiveDiff;
  sideBySide: boolean;
  onToggleSideBySide: () => void;
  onResolve: (kept: boolean, finalContents: string) => void;
}): JSX.Element {
  let container!: HTMLDivElement;
  let diffEditor: monaco.editor.IStandaloneDiffEditor | undefined;
  let originalModel: monaco.editor.ITextModel | undefined;
  let modifiedModel: monaco.editor.ITextModel | undefined;
  let offFonts: (() => void) | undefined;
  let disposed = false;

  onMount(() => {
    onCleanup(() => {
      disposed = true;
      offFonts?.();
      diffEditor?.dispose();
      modifiedModel?.dispose();
      originalModel?.dispose();
    });

    // This component creates its own Monaco diff editor, so the VSCode service layer must be
    // initialized first. The editor host does that at startup; awaiting here (idempotent) keeps the
    // diff correct even if one arrives before the host loaded, now that there's no global init gate.
    void initEditorServices().then(() => {
      if (disposed) {
        return;
      }
      // The diff is an editor surface, so it uses the resolved editor font (host setting) and tracks
      // live changes while open.
      const font = currentFonts().editor;
      diffEditor = monaco.editor.createDiffEditor(container, {
        theme: "vs-dark",
        automaticLayout: true,
        readOnly: false,
        originalEditable: false,
        renderSideBySide: props.sideBySide,
        fontSize: font.size,
        fontFamily: font.family,
        fontWeight: font.weight,
        minimap: { enabled: false },
      });
      offFonts = onFontsChanged((config) =>
        diffEditor?.updateOptions({
          fontFamily: config.editor.family,
          fontSize: config.editor.size,
          fontWeight: config.editor.weight,
        }),
      );

      // Keep the file: scheme + path (so Monaco infers the language from the extension exactly as before)
      // but tag the URI with a query so it can't collide with the main editor's real file:// model for the
      // same path. The editor backs the open file with that model; if the diff shared the URI, mounting
      // would dispose the editor's model and tearing the diff down on Keep would dispose it again, leaving
      // the editor on a dead model (a blank pane). The query keeps the two separate; this dispose only ever
      // clears a stale model from a prior diff of the same file.
      const uri = monaco.Uri.file(props.diff.path).with({ query: "weavie-diff" });
      monaco.editor.getModel(uri)?.dispose();
      modifiedModel = monaco.editor.createModel(props.diff.proposed, undefined, uri);
      originalModel = monaco.editor.createModel(props.diff.original, modifiedModel.getLanguageId());
      diffEditor.setModel({ original: originalModel, modified: modifiedModel });
      diffEditor.getModifiedEditor().focus();
    });
  });

  // Flip inline ⇆ side-by-side live, without recreating the editor (Monaco re-lays out on the option change).
  createEffect(() => {
    const sideBySide = props.sideBySide;
    diffEditor?.updateOptions({ renderSideBySide: sideBySide });
  });

  const keep = (): void => props.onResolve(true, modifiedModel?.getValue() ?? props.diff.proposed);
  const reject = (): void => props.onResolve(false, "");

  return (
    <div class="diff-overlay">
      <div class="diff-bar">
        <span class="diff-title">{props.diff.tabName}</span>
        <span class="diff-path">{props.diff.path}</span>
        <span class="diff-actions">
          <button
            type="button"
            class="diff-mode"
            onClick={() => props.onToggleSideBySide()}
            title="Toggle inline / side-by-side diff"
          >
            {props.sideBySide ? "Inline" : "Side-by-side"}
          </button>
          <button type="button" class="keep" onClick={keep}>
            Keep ⏎
          </button>
          <button type="button" class="reject" onClick={reject}>
            Reject ⎋
          </button>
        </span>
      </div>
      <div class="diff-editor" ref={container} />
    </div>
  );
}
