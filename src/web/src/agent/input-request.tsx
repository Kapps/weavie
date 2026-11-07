import { createEffect, type JSX, onCleanup } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { AgentInputAnswers } from "./AgentInputDrafts";

export interface InputRequestProps {
  session: ClientSession;
  message: AgentPaneUpdate;
  // Only the newest pending request answers to the shortcuts; older cards must not advertise them.
  answersToKeys: boolean;
}

/** Sends the request's resolution; false when the request has no id to answer. */
export function resolveInput(
  props: InputRequestProps,
  action: "accept" | "decline" | "cancel",
  answers: AgentInputAnswers,
): boolean {
  const requestId = props.message.requestId;
  if (requestId === null || requestId === undefined || requestId.length === 0) {
    return false;
  }
  props.session.feature("agent").publish("input", { requestId, action, answers });
  return true;
}

/** The key label for a command this card answers to, or "" when another card owns the keys. */
export function ownedKeyLabel(props: InputRequestProps, commandId: string): string {
  return props.answersToKeys ? liveKeyLabel(commandId) : "";
}

export function withKey(label: string, key: string): string {
  return key === "" ? label : `${label} (${key})`;
}

/** Registers the card's commands while it owns the keys. */
export function useInputCommands(
  props: InputRequestProps,
  commands: () => ReadonlyMap<string, (args: unknown) => boolean>,
): void {
  createEffect(() => {
    if (!props.answersToKeys) {
      return;
    }
    const all = new Map<string, (args: unknown) => boolean>([
      [CommandIds.agentDeclineInput, () => resolveInput(props, "decline", {})],
      [CommandIds.agentCancelInput, () => resolveInput(props, "cancel", {})],
      ...commands(),
    ]);
    const off = [...all].map(([id, handler]) => registerCommand(id, handler));
    onCleanup(() => off.forEach((dispose) => void dispose()));
  });
}

/** Decline and Cancel, titled with their keys while this card owns them. */
export function DismissButtons(props: InputRequestProps): JSX.Element {
  return (
    <>
      <button
        type="button"
        title={withKey("Decline request", ownedKeyLabel(props, CommandIds.agentDeclineInput))}
        onClick={() => resolveInput(props, "decline", {})}
      >
        Decline
      </button>
      <button
        type="button"
        title={withKey("Cancel request", ownedKeyLabel(props, CommandIds.agentCancelInput))}
        onClick={() => resolveInput(props, "cancel", {})}
      >
        Cancel
      </button>
    </>
  );
}
