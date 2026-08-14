import { createEffect, createSignal, type JSX, onCleanup, Show } from "solid-js";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { formatElapsed, type PendingRequestKind } from "./turn-progress";

/** Live turn state shared by the desktop composer and compact pane header. */
export function AgentWorkingStatus(props: {
  compact: boolean;
  pendingKind: PendingRequestKind | null;
  turnActive: boolean;
  turnStartedAt: number | null;
}): JSX.Element {
  const [now, setNow] = createSignal(Date.now());
  createEffect(() => {
    if (props.turnStartedAt === null) {
      return;
    }
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    onCleanup(() => clearInterval(timer));
  });
  const elapsed = (): string => {
    const started = props.turnStartedAt;
    return started === null ? "" : formatElapsed(now() - started);
  };
  const label = (): string => workingLabel(props.pendingKind);
  const interruptKey = (): string => liveKeyLabel(CommandIds.agentInterrupt);

  return (
    <Show when={props.turnActive}>
      <div
        class="agent-working"
        classList={{ waiting: props.pendingKind !== null, "agent-working-compact": props.compact }}
        title={props.compact ? label() : undefined}
      >
        <span class="agent-working-spinner" aria-hidden="true" />
        <span class="agent-working-label" role="status">
          {label()}
        </span>
        <Show when={props.turnStartedAt !== null}>
          <span class="agent-working-time">{elapsed()}</span>
        </Show>
        <Show when={!props.compact && interruptKey() !== ""}>
          <span class="agent-working-hint">{interruptKey()} to interrupt</span>
        </Show>
      </div>
    </Show>
  );
}

function workingLabel(pending: PendingRequestKind | null): string {
  if (pending === "approval") {
    return "Waiting on your approval";
  }
  if (pending === "authentication") {
    return "Waiting for sign in";
  }
  return pending === "input" ? "Waiting on your answer" : "Working";
}
