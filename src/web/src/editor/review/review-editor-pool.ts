import { FindController } from "@codingame/monaco-vscode-api/vscode/vs/editor/contrib/find/browser/findController";
import { createEmbeddedEditor, monaco } from "../monaco-setup";

interface Slot {
  editor: monaco.editor.IStandaloneCodeEditor;
  mount: HTMLDivElement;
  overrides: monaco.editor.IEditorOptions;
}

export interface ReviewEditorLease {
  editor: monaco.editor.IStandaloneCodeEditor;
  mount: HTMLDivElement;
  release(): void;
}

export interface ReviewEditorPool {
  acquire(
    container: HTMLElement,
    model: monaco.editor.ITextModel,
    editable: boolean,
  ): ReviewEditorLease;
  dispose(): void;
}

/** Retains editor shells up to the review's peak concurrent leases, never file models. */
export function createReviewEditorPool(): ReviewEditorPool {
  const slots = new Set<Slot>();
  const idle: Slot[] = [];
  let disposed = false;
  const create = (container: HTMLElement): Slot => {
    const mount = document.createElement("div");
    mount.className = "unified-review-editor-viewport";
    mount.style.visibility = "hidden";
    container.append(mount);
    const horizontalScrollbarSize =
      monaco.editor.EditorOptions.scrollbar.defaultValue.horizontalScrollbarSize;
    const overrides: monaco.editor.IEditorOptions = {
      readOnly: true,
      scrollBeyondLastLine: false,
      automaticLayout: false,
      smoothScrolling: false,
      overviewRulerLanes: 0,
      overviewRulerBorder: false,
      hideCursorInOverviewRuler: true,
      minimap: { enabled: false },
      folding: false,
      stickyScroll: { enabled: false },
      renderLineHighlightOnlyWhenFocus: true,
      // Visible-line width changes must not change the section's height.
      scrollbar: { horizontalScrollbarSize, ignoreHorizontalScrollbarInContentHeight: true },
      padding: { top: 6, bottom: 6 + horizontalScrollbarSize },
    };
    const slot = { mount, overrides, editor: createEmbeddedEditor(mount, null, overrides) };
    slots.add(slot);
    return slot;
  };
  return {
    acquire: (container, model, editable) => {
      if (disposed) throw new Error("Cannot lease an editor from a disposed review pool.");
      const slot = idle.pop() ?? create(container);
      const { editor, mount, overrides } = slot;
      mount.style.visibility = "hidden";
      container.append(mount);
      // Live settings reapply this same options object, including the current lease's editability.
      overrides.readOnly = !editable;
      editor.updateOptions({ readOnly: overrides.readOnly });
      editor.setModel(model);
      editor.setPosition({ lineNumber: 1, column: 1 });
      editor.setScrollPosition({ scrollTop: 0, scrollLeft: 0 }, monaco.editor.ScrollType.Immediate);
      let released = false;
      return {
        editor,
        mount,
        release: () => {
          if (released) return;
          released = true;
          if (disposed) return;
          editor.setModel(null);
          editor.getContribution<FindController>(FindController.ID)?.getState().change(
            {
              isRevealed: false,
              isReplaceRevealed: false,
              searchString: "",
              replaceString: "",
              searchScope: null,
            },
            false,
          );
          mount.remove();
          mount.style.removeProperty("top");
          idle.push(slot);
        },
      };
    },
    dispose: () => {
      disposed = true;
      for (const { editor, mount } of slots) {
        editor.dispose();
        mount.remove();
      }
      slots.clear();
      idle.length = 0;
    },
  };
}
