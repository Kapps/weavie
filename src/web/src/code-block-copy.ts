// Selecting a line inside a code block copies a trailing newline the user never selected: the engine appends
// it for the block boundary, not from the markup (trimming the rendered text changes nothing). Pasting that
// into a shell runs the command instead of leaving it on the prompt, so drop it as the copy is written.

/** The text a code-block copy should carry, or null to leave the clipboard alone. */
export function codeBlockCopyText(selected: string): string | null {
  // Only the one newline the boundary added — a selection genuinely ending in a blank line keeps it.
  return selected.endsWith("\n") && !selected.endsWith("\n\n") ? selected.slice(0, -1) : null;
}

/** Strips the block-boundary newline from copies made inside a code block. Returns an uninstall function. */
export function installCodeBlockCopy(): () => void {
  const controller = new AbortController();
  document.addEventListener("copy", onCopy, { signal: controller.signal });
  return () => controller.abort();
}

function onCopy(event: ClipboardEvent): void {
  const selection = getSelection();
  if (selection === null || selection.rangeCount === 0 || selection.isCollapsed) {
    return;
  }

  if (!selectsOnlyCode(selection.getRangeAt(0))) {
    return;
  }

  const text = codeBlockCopyText(selection.toString());
  if (text === null) {
    return;
  }

  event.clipboardData?.setData("text/plain", text);
  event.preventDefault();
}

// A line selected within a block keeps both endpoints inside it, but selecting a single-line block takes the
// whole element — so its endpoints land outside and only the copied content identifies it as code.
function selectsOnlyCode(range: Range): boolean {
  const start = enclosingPre(range.startContainer);
  if (start !== null && start === enclosingPre(range.endContainer)) {
    return true;
  }

  const content = range.cloneContents();
  if (content.querySelector("pre") === null) {
    return false;
  }

  const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    if (node.textContent?.trim() !== "" && enclosingPre(node) === null) {
      return false;
    }
  }

  return true;
}

function enclosingPre(node: Node): Element | null {
  const element = node instanceof Element ? node : node.parentElement;
  return element?.closest("pre") ?? null;
}
