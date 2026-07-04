import { createMemo, createSignal } from "solid-js";
import { activeBackendId, onSessionMessage, type Suggestion } from "../bridge";

// Each backend pushes its own contextual suggestions; keep them keyed by backend and surface only the active
// one's cards (the workspace the user is looking at). Top-level signal so it survives HMR.
const [byBackend, setByBackend] = createSignal<Map<string, Suggestion[]>>(new Map());

onSessionMessage((message, backendId) => {
  if (message.type === "suggestions") {
    setByBackend((prev) => {
      const next = new Map(prev);
      next.set(backendId, message.items);
      return next;
    });
  }
});

/** The active backend's contextual suggestions (empty until its first push). */
export const suggestions = createMemo<Suggestion[]>(() => byBackend().get(activeBackendId()) ?? []);
