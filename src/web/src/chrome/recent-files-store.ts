import { createMemo, createSignal } from "solid-js";
import { activeBackendId, onSessionMessage } from "../bridge";

// Each backend pushes its own frecency-ranked recent files (absolute paths meaningful only on the box that
// produced them); keep them keyed by backend and surface only the active one's. Top-level signal so it survives HMR.
const [byBackend, setByBackend] = createSignal<Map<string, readonly string[]>>(new Map());

onSessionMessage((message, backendId) => {
  if (message.type === "recent-files") {
    setByBackend((prev) => {
      const next = new Map(prev);
      next.set(backendId, message.files);
      return next;
    });
  }
});

/// Most-frecent-first absolute paths of the active backend's recently used files (host-ranked). Empty until its
/// first push.
export const recentFiles = createMemo<readonly string[]>(
  () => byBackend().get(activeBackendId()) ?? [],
);
