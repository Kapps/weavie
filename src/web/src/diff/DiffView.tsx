import { type JSX, onCleanup, onMount } from "solid-js";
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

// Renders an inbound openDiff as an editable Monaco diff: original (read-only, left) vs the
// proposed contents (editable, right). The user can tweak the proposal before keeping. Keep ->
// the host saves the (edited) modified side and replies FILE_SAVED to Claude; Reject -> DIFF_REJECTED.
export function DiffView(props: {
  diff: ActiveDiff;
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
        renderSideBySide: true,
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

      // Use a file URI for the modified side so Monaco infers the language from the extension.
      const uri = monaco.Uri.file(props.diff.path);
      monaco.editor.getModel(uri)?.dispose();
      modifiedModel = monaco.editor.createModel(props.diff.proposed, undefined, uri);
      originalModel = monaco.editor.createModel(props.diff.original, modifiedModel.getLanguageId());
      diffEditor.setModel({ original: originalModel, modified: modifiedModel });
      diffEditor.getModifiedEditor().focus();
    });
  });

  const keep = (): void => props.onResolve(true, modifiedModel?.getValue() ?? props.diff.proposed);
  const reject = (): void => props.onResolve(false, "");

  return (
    <div class="diff-overlay">
      <div class="diff-bar">
        <span class="diff-title">{props.diff.tabName}</span>
        <span class="diff-path">{props.diff.path}</span>
        <span class="diff-actions">
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
