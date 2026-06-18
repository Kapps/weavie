import { X } from "lucide-solid";
import { For, type JSX, Show, createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { monaco } from "../editor/monaco-setup";
import { initEditorServices } from "../editor/vscode-services";
import { currentFonts, onFontsChanged } from "../fonts";

// Inline diff for now; a future setting flips this to side-by-side. Kept as a module constant so the
// toggle is a one-line change, not a rewrite of the diff editor wiring.
const RENDER_SIDE_BY_SIDE = false;

// One changed file in the session list: path + the line counts the host computed.
export interface ChangeFile {
  path: string;
  name: string;
  added: number;
  removed: number;
}

// One file's session diff: its baseline (content at first touch) vs. its current content. `current` only
// seeds the live model when the file isn't open yet — once a model exists, the diff shows the LIVE buffer.
export interface ChangeDiff {
  path: string;
  name: string;
  baseline: string;
  current: string;
}

// A real Monaco diff editor whose MODIFIED side is the file's live `file://` model (shared with the main
// editor), so it carries syntax highlighting, LSP, and the same dirty buffer — and edits made here flow
// straight back to the editor + autosave. The ORIGINAL side is the read-only session baseline.
function InlineDiff(props: {
  diff: ChangeDiff;
  getFileModel: (path: string, seed: string) => monaco.editor.ITextModel | undefined;
}): JSX.Element {
  let container!: HTMLDivElement;
  let diffEditor: monaco.editor.IStandaloneDiffEditor | undefined;
  // The baseline (original) model is ours to own and dispose. The modified model is the live file model,
  // owned by the editor host for the session — we must NEVER dispose it (that would blank the main editor).
  let baselineModel: monaco.editor.ITextModel | undefined;
  let offFonts: (() => void) | undefined;
  let disposed = false;
  const [ready, setReady] = createSignal(false);

  // Rebuild the diff's model pair for the currently selected file. The modified side is fetched live; if
  // the host isn't ready yet it returns undefined and we leave the editor empty until the next run.
  const applyModel = (): void => {
    if (diffEditor === undefined) {
      return;
    }
    const modified = props.getFileModel(props.diff.path, props.diff.current);
    if (modified === undefined) {
      return;
    }
    const previousBaseline = baselineModel;
    baselineModel = monaco.editor.createModel(props.diff.baseline, modified.getLanguageId());
    diffEditor.setModel({ original: baselineModel, modified });
    previousBaseline?.dispose();
  };

  onMount(() => {
    onCleanup(() => {
      disposed = true;
      offFonts?.();
      diffEditor?.dispose();
      baselineModel?.dispose();
    });

    // This component creates its own Monaco diff editor, so the VSCode service layer must be initialized
    // first (idempotent; the editor host also does it at startup).
    void initEditorServices().then(() => {
      if (disposed) {
        return;
      }
      const font = currentFonts().editor;
      diffEditor = monaco.editor.createDiffEditor(container, {
        theme: "vs-dark",
        automaticLayout: true,
        readOnly: false,
        originalEditable: false,
        renderSideBySide: RENDER_SIDE_BY_SIDE,
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
      setReady(true);
    });
  });

  // Re-apply when the editor becomes ready or the selected file changes (applyModel reads props.diff, so
  // its fields are tracked each time it runs).
  createEffect(() => {
    if (ready()) {
      applyModel();
    }
  });

  return <div class="changes-diff-editor" ref={container} />;
}

// The session changes overlay: a file list (with +/- counts) beside the selected file's live diff editor.
export default function ChangesPanel(props: {
  files: ChangeFile[];
  diff: ChangeDiff | null;
  getFileModel: (path: string, seed: string) => monaco.editor.ITextModel | undefined;
  onSelect: (path: string) => void;
  onClose: () => void;
}): JSX.Element {
  return (
    <div class="changes-panel">
      <div class="changes-head">
        <span class="changes-title">Session changes ({props.files.length})</span>
        <button type="button" class="changes-close" onClick={() => props.onClose()}>
          <X />
        </button>
      </div>
      <div class="changes-body">
        <ul class="changes-list">
          <For
            each={props.files}
            fallback={<li class="changes-empty">No changes yet this session.</li>}
          >
            {(file) => (
              <li class="changes-row">
                <button
                  type="button"
                  classList={{ "changes-item": true, active: props.diff?.path === file.path }}
                  onClick={() => props.onSelect(file.path)}
                >
                  <span class="cf-name" title={file.path}>
                    {file.name}
                  </span>
                  <span class="cf-stat">
                    <span class="cf-add">+{file.added}</span>
                    <span class="cf-del">−{file.removed}</span>
                  </span>
                </button>
              </li>
            )}
          </For>
        </ul>
        <div class="changes-diff">
          <Show
            when={props.diff}
            fallback={<div class="changes-hint">Select a file to see its session diff.</div>}
          >
            {(diff) => <InlineDiff diff={diff()} getFileModel={props.getFileModel} />}
          </Show>
        </div>
      </div>
    </div>
  );
}
