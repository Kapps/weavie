import { createMemo, createSignal } from "solid-js";
import { registerHostFeature, selectedSession } from "../bridge";
import { selectedFileIndex } from "../files/session-files";

// Each backend pushes checkout-relative paths so one workspace-wide history follows files across sessions.
// Keep them keyed by backend and resolve the active one's paths against the selected checkout.
const [byBackend, setByBackend] = createSignal<Map<string, readonly string[]>>(new Map());

registerHostFeature((connection) =>
  connection.host.feature("recentFiles").on<{ files: string[] }>("changed", ({ files }) => {
    setByBackend((prev) => {
      const next = new Map(prev);
      next.set(connection.id, files);
      return next;
    });
  }),
);

/** Most-frecent-first absolute paths of files in the selected checkout. */
export const recentFiles = createMemo<readonly string[]>(() => {
  const root = selectedFileIndex().root;
  if (root === null) return [];
  const separator = root.includes("\\") ? "\\" : "/";
  const base = root.replace(/[\\/]+$/, "");
  return (byBackend().get(selectedSession()?.connection.id ?? "") ?? []).map(
    (path) => `${base}${separator}${path.replace(/[\\/]/g, separator)}`,
  );
});
