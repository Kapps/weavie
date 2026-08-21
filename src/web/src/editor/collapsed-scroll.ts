// Monaco measures its container as `max(5, clientHeight)` (ElementSizeObserver), so a container that is
// momentarily 0-height — a pane mid-mount, a tab being shown — latches a 5px viewport. Anything that reveals a
// line while it's that short scrolls toward the end of the file, and the offset it lands on stays valid once
// the real height returns: `scrollBeyondLastLine` bounds scrollTop by content height, not viewport height, so
// nothing walks it back. The editor ends up full-size and parked past the last line, rendering blank space.
//
// A scroll made while the editor couldn't show a single line is an artifact of the measurement, never the
// user's intent, so the offset from the last usable viewport is restored when one comes back.

import { monaco } from "./monaco-setup";

/** Binds the collapsed-viewport scroll guard to `editor`. Disposed with the editor. */
export function guardCollapsedScroll(editor: monaco.editor.IStandaloneCodeEditor): void {
  const showsALine = (height: number): boolean =>
    height >= editor.getOption(monaco.editor.EditorOption.lineHeight);

  let wasUsable = showsALine(editor.getLayoutInfo().height);
  let intended = editor.getScrollTop();

  const subscriptions = [
    editor.onDidScrollChange(() => {
      if (wasUsable) {
        intended = editor.getScrollTop();
      }
    }),
    // A new file scrolls where it scrolls; the outgoing file's offset says nothing about it.
    editor.onDidChangeModel(() => {
      intended = editor.getScrollTop();
    }),
    editor.onDidLayoutChange((layout) => {
      const usable = showsALine(layout.height);
      if (usable && !wasUsable) {
        wasUsable = true;
        editor.setScrollTop(intended);
        return;
      }
      wasUsable = usable;
    }),
  ];
  editor.onDidDispose(() => {
    for (const subscription of subscriptions) {
      subscription.dispose();
    }
  });
}
