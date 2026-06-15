import { type JSX, onCleanup, onMount } from "solid-js";
import { monaco } from "../editor/monaco-setup";

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

  onMount(() => {
    diffEditor = monaco.editor.createDiffEditor(container, {
      theme: "vs-dark",
      automaticLayout: true,
      readOnly: false,
      originalEditable: false,
      renderSideBySide: true,
      fontSize: 13,
      fontFamily: 'ui-monospace, "SF Mono", Menlo, monospace',
      minimap: { enabled: false },
    });

    // Use a file URI for the modified side so Monaco infers the language from the extension.
    const uri = monaco.Uri.file(props.diff.path);
    monaco.editor.getModel(uri)?.dispose();
    modifiedModel = monaco.editor.createModel(props.diff.proposed, undefined, uri);
    originalModel = monaco.editor.createModel(props.diff.original, modifiedModel.getLanguageId());
    diffEditor.setModel({ original: originalModel, modified: modifiedModel });
    diffEditor.getModifiedEditor().focus();

    onCleanup(() => {
      diffEditor?.dispose();
      modifiedModel?.dispose();
      originalModel?.dispose();
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
