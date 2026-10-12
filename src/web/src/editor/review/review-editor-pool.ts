import { CommonFindController } from "@codingame/monaco-vscode-api/vscode/vs/editor/contrib/find/browser/findController";
import { createEmbeddedEditor, monaco } from "../monaco-setup";
import type { ReviewScroll } from "./review-scroll";

/** One pooled editor, leased to a single section until released. */
export interface ReviewEditorSlot {
  readonly editor: monaco.editor.IStandaloneCodeEditor;
  /** Placed in the section's container for the lease. */
  readonly mount: HTMLElement;
  /** The editor's fixed-widget layer, in the review scroller for the slot's life. */
  readonly widgets: HTMLElement;
  /** Aborts when the lease is released. */
  readonly signal: AbortSignal;
  /** Detaches the model and returns the editor to the pool; idempotent. */
  release(): void;
}

export interface ReviewEditorPool {
  lease(): ReviewEditorSlot;
  dispose(): void;
}

function find(editor: monaco.editor.ICodeEditor): CommonFindController {
  const controller = CommonFindController.get(editor);
  if (controller === null) throw new Error("Monaco find contribution is not registered.");
  return controller;
}

interface PooledEditor {
  editor: monaco.editor.IStandaloneCodeEditor;
  mount: HTMLElement;
  widgets: HTMLElement;
}

// Editors outlive the sections that show them: creating and disposing one per section mount adds and removes
// per-editor stylesheets, and WebKit restyles the whole document for each removal.
export function createReviewEditorPool(scroller: ReviewScroll): ReviewEditorPool {
  const free: PooledEditor[] = [];
  let closed = false;
  const destroy = ({ editor, widgets }: PooledEditor): void => {
    editor.dispose();
    widgets.remove();
  };
  const create = (): PooledEditor => {
    const mount = document.createElement("div");
    mount.className = "unified-review-editor-viewport";
    // Fixed widgets must escape the transformed scroll content; each editor owns its widget focus.
    const widgets = document.createElement("div");
    widgets.className = "monaco-editor unified-review-overflow-widgets";
    scroller.element.append(widgets);
    const horizontalScrollbarSize =
      monaco.editor.EditorOptions.scrollbar.defaultValue.horizontalScrollbarSize;
    const editor = createEmbeddedEditor(
      mount,
      { dimension: { width: 0, height: 0 }, overflowWidgetsDomNode: widgets },
      {
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
      },
    );
    return { editor, mount, widgets };
  };
  return {
    lease: () => {
      const pooled = free.pop() ?? create();
      const lifetime = new AbortController();
      pooled.mount.style.visibility = "hidden";
      return {
        ...pooled,
        signal: lifetime.signal,
        release: () => {
          if (lifetime.signal.aborted) return;
          lifetime.abort();
          // Find would reopen on the next file the editor shows; closeFindWidget would also focus the editor.
          find(pooled.editor).getState().change({ isRevealed: false, searchScope: null }, false);
          pooled.editor.setModel(null);
          pooled.mount.remove();
          if (closed) destroy(pooled);
          else free.push(pooled);
        },
      };
    },
    // A leased editor stays usable until its section releases it, whichever is torn down first.
    dispose: () => {
      closed = true;
      for (const pooled of free.splice(0)) destroy(pooled);
    },
  };
}
