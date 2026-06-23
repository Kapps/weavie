import { createSignal } from "solid-js";
import { onHostMessage } from "../bridge";

// The host's frecency-ranked recent files (most-relevant-first absolute paths), pushed on `ready` and whenever
// the active file changes. A module singleton (like session-store) so it survives HMR and any component can read
// it without prop-drilling through the title bars.
const [recentFiles, setRecentFiles] = createSignal<readonly string[]>([]);

onHostMessage((message) => {
  if (message.type === "recent-files") {
    setRecentFiles(message.files);
  }
});

/// Most-frecent-first absolute paths of recently used files (host-ranked). Empty until the first push.
export { recentFiles };
