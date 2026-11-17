import { For, type JSX, Show } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { showForReview } from "../editor/view-mode-store";
import { revealFileIn } from "../files/reveal";
import { planIdentity } from "./agent-plan";

export function EditLocationActions(props: {
  session: ClientSession | null;
  message: AgentPaneUpdate;
}): JSX.Element {
  const targets = (): { path: string; line: number | undefined }[] => {
    const resolved = new Map<string, { path: string; line: number | undefined }>();
    for (const location of props.message.locations ?? []) {
      resolved.set(location.path, { path: location.path, line: location.line ?? undefined });
    }
    for (const diff of props.message.diffs ?? []) {
      if (!resolved.has(diff.path)) {
        resolved.set(diff.path, { path: diff.path, line: undefined });
      }
    }
    return [...resolved.values()];
  };

  const review = (location: { path: string; line: number | undefined }): void => {
    if (props.session !== null) showForReview(props.session, location.path);
    revealFileIn(props.session, location.path, location.line, true);
  };

  return (
    <Show when={targets().length > 0}>
      <div class="agent-approval-actions">
        <For each={targets()}>
          {(target) => (
            <button
              type="button"
              class="ui-button"
              title={`Review ${target.path}`}
              onClick={() => review(target)}
            >
              Review {targets().length > 1 ? target.path.split(/[\\/]/).at(-1) : "edit"}
            </button>
          )}
        </For>
      </div>
    </Show>
  );
}

export function PlanActions(props: {
  message: AgentPaneUpdate;
  session: ClientSession | null;
}): JSX.Element {
  const identity = (): ReturnType<typeof planIdentity> => planIdentity(props.message);
  const key = (): string => liveKeyLabel(CommandIds.openAgentPlan);
  const open = (): void => {
    const plan = identity();
    if (props.session !== null && plan !== null) {
      void props.session.feature("agent").request("openPlan", plan);
    }
  };

  return (
    <Show when={identity()}>
      <div class="agent-approval-actions">
        <button
          type="button"
          class="ui-button"
          title={key() === "" ? "Open plan in editor" : `Open plan in editor (${key()})`}
          onClick={open}
        >
          Open plan
          <Show when={key() !== ""}>
            <span class="agent-key-chip ui-kbd ui-kbd-cap" aria-hidden="true">
              {key()}
            </span>
          </Show>
        </button>
      </div>
    </Show>
  );
}
