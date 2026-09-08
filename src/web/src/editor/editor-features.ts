import type { ClientSession } from "../bridge";
import { installAltClickPeek } from "./alt-click-peek";
import { activeEditor, observeEditors } from "./editor-instances";
import { installEditorSelection } from "./editor-selection";
import { createGitBlame } from "./git-blame";
import type { monaco } from "./monaco-setup";
import { createReviseMarks, createReviseState, type ReviseRegion } from "./revise-marks";
import { createSpellCheck } from "./spell-check";

/** Shared document features follow the editor's lifetime and commands follow its retained focus. */
export function createEditorFeatures() {
  const revisions = createReviseState();
  const features = new Map<
    monaco.editor.ICodeEditor,
    {
      spelling: ReturnType<typeof createSpellCheck>;
      blame: ReturnType<typeof createGitBlame>;
      revise: ReturnType<typeof createReviseMarks>;
    }
  >();
  const dispose = observeEditors((editor) => {
    const selection = installEditorSelection(editor);
    const peek = installAltClickPeek(editor);
    const spelling = createSpellCheck(editor);
    const blame = createGitBlame(editor);
    const revise = createReviseMarks(editor, revisions);
    features.set(editor, { spelling, blame, revise });
    return () => {
      features.delete(editor);
      selection();
      peek.dispose();
      spelling.dispose();
      blame.dispose();
      revise.dispose();
    };
  });
  return {
    active: () => {
      const editor = activeEditor();
      return editor === null ? undefined : features.get(editor);
    },
    setRegions: (session: ClientSession, current: ReviseRegion[]): void => {
      revisions.set(session, current);
      for (const feature of features.values()) feature.revise.refresh();
    },
    verifyRevision: (session: ClientSession, id: number): string | null => {
      for (const feature of features.values()) {
        const refusal = feature.revise.verify(session, id);
        if (refusal !== null) return refusal;
      }
      return null;
    },
    dispose: () => {
      dispose();
      revisions.dispose();
    },
  };
}

export type EditorFeatures = ReturnType<typeof createEditorFeatures>;
