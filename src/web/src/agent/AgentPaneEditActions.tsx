import { For, type JSX, Show } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { revealFileIn } from "../files/reveal";
import { KeyedButton } from "./AgentPaneActions";
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
    revealFileIn(props.session, location.path, location.line, true);
  };

  return (
    <Show when={targets().length > 0}>
      <div class="agent-approval-actions">
        <For each={targets()}>
          {(target) => (
            <button type="button" title={`Review ${target.path}`} onClick={() => review(target)}>
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
  const open = (): void => {
    const plan = identity();
    if (props.session !== null && plan !== null) {
      void props.session.feature("agent").request("openPlan", plan);
    }
  };

  return (
    <Show when={identity()}>
      <div class="agent-approval-actions">
        <KeyedButton
          label="Open plan"
          keyLabel={liveKeyLabel(CommandIds.openAgentPlan)}
          disabled={false}
          onClick={open}
        />
      </div>
    </Show>
  );
}
