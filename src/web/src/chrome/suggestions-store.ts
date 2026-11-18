import { createMemo } from "solid-js";
import { type ClientSession, type Suggestion, selectedSession } from "../bridge";
import { repoStateOf } from "../repo-state";

/** The contextual suggestions of the selected session's repo, with the session they are shown for. */
export const suggestions = createMemo<{ session: ClientSession | null; items: Suggestion[] }>(
  () => {
    const session = selectedSession();
    return { session, items: repoStateOf(session).suggestions };
  },
);
