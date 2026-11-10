import { createEffect, createMemo, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";

export function ApprovalActions(props: {
  session: ClientSession | null;
  message: Pick<AgentPaneUpdate, "requestId" | "actions">;
  // The chords answer only the newest pending approval; older cards must not advertise them.
  answersToKeys: boolean;
  // Runs before an allow answer is sent, e.g. to save the plan being approved.
  beforeAllow: (send: () => void) => void;
}): JSX.Element {
  const choose = (optionId: string): void => {
    const requestId = props.message.requestId;
    const session = props.session;
    if (
      session === null ||
      requestId === null ||
      requestId === undefined ||
      requestId.length === 0
    ) {
      return;
    }
    const send = (): void =>
      session.feature("agent").publish("permission", { requestId, optionId });
    const kind = props.message.actions?.find((action) => action.id === optionId)?.kind;
    if (kind?.startsWith("allow_")) props.beforeAllow(send);
    else send();
  };

  const commandFor = (kind: string): string | null => {
    switch (kind) {
      case "allow_once":
        return CommandIds.agentApprove;
      case "allow_always":
        return CommandIds.agentApproveForSession;
      case "reject_once":
      case "reject_always":
        return CommandIds.agentDecline;
      default:
        return null;
    }
  };

  // Each chord answers the first option of its kind; only that option advertises it.
  const bound = createMemo(() => {
    const commands = new Map<string, string>();
    for (const action of props.message.actions ?? []) {
      const command = commandFor(action.kind);
      if (command !== null && ![...commands.values()].includes(command)) {
        commands.set(action.id, command);
      }
    }
    return commands;
  });

  createEffect(() => {
    if (!props.answersToKeys) {
      return;
    }
    const off = [...bound()].map(([optionId, command]) =>
      registerCommand(command, () => {
        choose(optionId);
        return true;
      }),
    );
    onCleanup(() => off.forEach((dispose) => void dispose()));
  });

  const decision = (label: string, value: string): JSX.Element => {
    const key = (): string => {
      const command = bound().get(value);
      return props.answersToKeys && command !== undefined ? liveKeyLabel(command) : "";
    };
    return (
      <button
        type="button"
        class="ui-button ui-button-content"
        classList={{ default: bound().get(value) === CommandIds.agentApprove }}
        title={key() === "" ? label : `${label} (${key()})`}
        onClick={() => choose(value)}
      >
        {label}
        <Show when={key() !== ""}>
          <kbd class="agent-key-chip ui-kbd">{key()}</kbd>
        </Show>
      </button>
    );
  };

  return (
    <div class="agent-approval-actions">
      <For each={props.message.actions ?? []}>{(action) => decision(action.label, action.id)}</For>
    </div>
  );
}

export function AuthenticationActions(props: {
  session: ClientSession;
  message: AgentPaneUpdate;
  answersToKeys: boolean;
}): JSX.Element {
  const authenticate = (methodId: string): boolean => {
    const requestId = props.message.requestId;
    if (requestId !== null && requestId !== undefined) {
      props.session.feature("agent").publish("authenticate", { requestId, methodId, answers: {} });
    }
    return true;
  };
  createEffect(() => {
    const first = props.message.actions?.[0];
    if (!props.answersToKeys || first === undefined) {
      return;
    }
    const off = registerCommand(CommandIds.agentAuthenticate, () => authenticate(first.id));
    onCleanup(off);
  });
  const firstId = (): string | null => props.message.actions?.[0]?.id ?? null;
  return (
    <div class="agent-approval-actions">
      <For each={props.message.actions ?? []}>
        {(action) => (
          <button
            type="button"
            class="ui-button ui-button-content"
            title={
              props.answersToKeys &&
              action.id === firstId() &&
              liveKeyLabel(CommandIds.agentAuthenticate) !== ""
                ? `${action.label} (${liveKeyLabel(CommandIds.agentAuthenticate)})`
                : action.label
            }
            onClick={() => authenticate(action.id)}
          >
            {action.label}
          </button>
        )}
      </For>
    </div>
  );
}
