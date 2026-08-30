// The text the user has highlighted, wherever it lives. Monaco and xterm each own their selection behind
// their own API (what reaches the document is an incidental mirror), so every surface registers a live
// reader and reports its changes; the most recently changed source with a live selection wins, so a
// highlight left behind in another pane never outranks a fresh one.

const readers = new Map<string, () => string>();
let recent: string[] = [];

/** Registers a surface's live selection reader (Monaco, an xterm, the document); the result deregisters it. */
export function registerSelectionSource(key: string, read: () => string): () => void {
  readers.set(key, read);
  return () => {
    // By identity: a pane remounting under the same key registers before the old one tears down, and a
    // blind delete would drop the live reader instead of the dead one.
    if (readers.get(key) !== read) {
      return;
    }
    readers.delete(key);
    recent = recent.filter((entry) => entry !== key);
  };
}

/** Reports that a source's selection changed, making it the one a highlight is read from. */
export function noteSelectionChange(key: string): void {
  recent = [key, ...recent.filter((entry) => entry !== key)];
}

/** Tracks plain DOM selections (the agent transcript, panels, anything not Monaco or an xterm). */
export function trackDocumentSelection(): () => void {
  const off = registerSelectionSource("document", () => document.getSelection()?.toString() ?? "");
  const onChange = (): void => noteSelectionChange("document");
  document.addEventListener("selectionchange", onChange);
  return () => {
    document.removeEventListener("selectionchange", onChange);
    off();
  };
}

/**
 * The highlighted text to seed a content search with: the freshest live selection, trimmed. Null when
 * nothing is highlighted or that highlight spans lines — a multi-line query can't match a line-based grep,
 * and seeding an older pane's selection instead would search something the user isn't looking at.
 */
export function selectedText(): string | null {
  for (const key of recent) {
    const text = (readers.get(key)?.() ?? "").trim();
    if (text !== "") {
      return text.includes("\n") ? null : text;
    }
  }
  return null;
}
