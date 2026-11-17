import { createMemo } from "solid-js";
import { type Suggestion, selectedSession } from "../bridge";
import { repoStateOf } from "../repo-state";

/** The contextual suggestions of the selected session's repo. */
export const suggestions = createMemo<Suggestion[]>(
  () => repoStateOf(selectedSession()).suggestions,
);
