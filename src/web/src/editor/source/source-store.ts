import { createSignal } from "solid-js";

// The fetched source documents (Notion pages), keyed by their target — the same key the source tab uses as its
// path/id. The host drives these through `source-loading` → `source-doc` (or `source-error`); SourceView renders
// the active tab's entry by status, TabStrip reads its title. Content lives only here (never persisted), mirroring
// how web tabs hold only their URL.
export interface SourceDocEntry {
  title: string;
  // The producing source's stable id, stamped by the host on source-loading/source-doc (e.g. "notion", "logs")
  // — the tab icon keys off it (source-icons.tsx).
  sourceId: string;
  // Exactly one body is set when ready: `markdown` (Notion — rendered to HTML by SourceView) or pre-rendered
  // `html` from the host (the log viewer), which SourceView sanitizes and injects as-is.
  markdown?: string | undefined;
  html?: string | undefined;
  // The page's last-edited time (ISO 8601), or "" when unknown — shown in the SourceView header.
  editedTime: string;
  status: "loading" | "ready" | "error";
  // Set when status is "error": the failure reason, shown in the tab instead of the spinner.
  message?: string;
}

const [docs, setDocs] = createSignal<Record<string, SourceDocEntry>>({});

export function setSourceLoading(target: string, title: string, sourceId: string): void {
  setDocs((prev) => ({
    ...prev,
    [target]: { title, sourceId, editedTime: "", status: "loading" },
  }));
}

export function setSourceDoc(
  target: string,
  doc: {
    title: string;
    sourceId: string;
    markdown?: string | undefined;
    html?: string | undefined;
    editedTime: string;
  },
): void {
  setDocs((prev) => ({ ...prev, [target]: { ...doc, status: "ready" } }));
}

export function setSourceError(target: string, message: string): void {
  setDocs((prev) => ({
    ...prev,
    // Keep the loading entry's guessed title + source id so the tab keeps its label and icon through the failure.
    [target]: {
      title: prev[target]?.title ?? "Notion",
      sourceId: prev[target]?.sourceId ?? "",
      editedTime: "",
      status: "error",
      message,
    },
  }));
}

export function sourceDoc(target: string): SourceDocEntry | undefined {
  return docs()[target];
}
