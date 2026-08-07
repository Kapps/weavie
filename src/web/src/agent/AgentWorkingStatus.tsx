import { createEffect, createMemo, createSignal, type JSX, onCleanup, Show } from "solid-js";
import type { AgentPaneUpdate } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { activeTurnStartedAt, formatElapsed, hasActiveTurn, pendingRequest } from "./turn-progress";

/** Live turn state shared by the desktop composer and compact pane header. */
export function AgentWorkingStatus(props: {
  compact: boolean;
  messages: AgentPaneUpdate[];
}): JSX.Element {
  const turnActive = createMemo(() => hasActiveTurn(props.messages));
  const pendingKind = createMemo(() => pendingRequest(props.messages)?.kind ?? null);
  const turnStartedAt = createMemo(() => activeTurnStartedAt(props.messages));
  const [now, setNow] = createSignal(Date.now());
  createEffect(() => {
    if (turnStartedAt() === null) {
      return;
    }
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    onCleanup(() => clearInterval(timer));
  });
  const elapsed = (): string => {
    const started = turnStartedAt();
    return started === null ? "" : formatElapsed(now() - started);
  };
  const label = (): string => workingLabel(pendingKind());
  const interruptKey = (): string => liveKeyLabel(CommandIds.agentInterrupt);

  return (
    <Show when={turnActive()}>
      <div
        class="agent-working"
        classList={{ waiting: pendingKind() !== null, "agent-working-compact": props.compact }}
        title={props.compact ? label() : undefined}
      >
        <span class="agent-working-spinner" aria-hidden="true" />
        <span class="agent-working-label" role="status">
          {label()}
        </span>
        <Show when={turnStartedAt() !== null}>
          <span class="agent-working-time">{elapsed()}</span>
        </Show>
        <Show when={!props.compact && interruptKey() !== ""}>
          <span class="agent-working-hint">{interruptKey()} to interrupt</span>
        </Show>
      </div>
    </Show>
  );
}

function workingLabel(pending: "approval" | "input" | null): string {
  if (pending === "approval") {
    return "Waiting on your approval";
  }
  return pending === "input" ? "Waiting on your answer" : "Working";
}
