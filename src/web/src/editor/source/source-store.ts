import { createSignal } from "solid-js";

// The fetched source documents (Notion pages), keyed by their target — the same key the source tab uses as its
// path/id. The host pushes these via `source-doc`; SourceView renders the active tab's entry, TabStrip reads its
// title. Content lives only here (never persisted), mirroring how web tabs hold only their URL.
export interface SourceDocEntry {
  title: string;
  html: string;
}

const [docs, setDocs] = createSignal<Record<string, SourceDocEntry>>({});

export function setSourceDoc(target: string, doc: SourceDocEntry): void {
  setDocs((prev) => ({ ...prev, [target]: doc }));
}

export function sourceDoc(target: string): SourceDocEntry | undefined {
  return docs()[target];
}
