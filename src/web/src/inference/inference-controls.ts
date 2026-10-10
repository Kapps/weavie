// The local host's suggestion settings: whether suggestions run, and the agent, model, effort, and Fast Mode they use.
// The host owns the options, asked of the agent itself; picking one writes the setting its axis id names.

import { createSignal } from "solid-js";
import { type InferenceControlsState, registerHostFeature } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { localFeature } from "../host-settings";

const [state, setState] = createSignal<InferenceControlsState | null>(null);

/** The latest pickers, or null until a surface has opened them. */
export const inferenceControls = state;

/** Whether the Configure Suggestions dialog is showing. */
export const [suggestionsDialogOpen, setSuggestionsDialogOpen] = createSignal(false);

registerCommand(CommandIds.configureSuggestions, () => {
  setSuggestionsDialogOpen(true);
});

registerHostFeature((connection) =>
  connection.isLocal
    ? connection.host.feature("inferenceControls").on<InferenceControlsState>("state", setState)
    : undefined,
);

const feature = () => localFeature("inferenceControls");

/** Shows the current pickers, asking the agent for its options the first time. */
export async function openInferenceControls(): Promise<void> {
  setState(await feature().request<InferenceControlsState>("open", {}));
}

/** Asks the agent for its options again, e.g. after it failed to answer. */
export function refreshInferenceControls(): Promise<void> {
  return feature().request<void>("refresh", {});
}
