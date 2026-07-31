import { createMemo, createSignal } from "solid-js";
import { registerHostFeature, type Suggestion, selectedSession } from "../bridge";

// Each backend pushes its own contextual suggestions; keep them keyed by backend and surface only the active
// one's cards (the workspace the user is looking at). Top-level signal so it survives HMR.
const [byBackend, setByBackend] = createSignal<Map<string, Suggestion[]>>(new Map());

registerHostFeature((connection) =>
  connection.host.feature("suggestions").on<{ items: Suggestion[] }>("changed", ({ items }) => {
    setByBackend((prev) => {
      const next = new Map(prev);
      next.set(connection.id, items);
      return next;
    });
  }),
);

/** The active backend's contextual suggestions (empty until its first push). */
export const suggestions = createMemo<Suggestion[]>(
  () => byBackend().get(selectedSession()?.connection.id ?? "") ?? [],
);
