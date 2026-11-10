import type { JSX } from "solid-js";
import { formatKey } from "../commands/keybindings";
import { dispatchCommand, findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { RailSession } from "./session-store";

// Stays until the session is reloaded: its state can't be trusted, so there is nothing to dismiss.
export function SessionReloadBar(props: { session: RailSession }): JSX.Element {
  const title = (): string => {
    const key = findCommand(CommandIds.reloadSessionPrompt)?.keys[0];
    return key === undefined ? "Reload session" : `Reload session (${formatKey(key)})`;
  };
  return (
    <output class="connection-banner connection-banner-error" role="alert">
      <span>{props.session.reloadRequired} This session needs a reload.</span>
      <button
        type="button"
        class="session-reload-button ui-button"
        title={title()}
        onClick={() =>
          void dispatchCommand(CommandIds.reloadSessionPrompt, {
            id: props.session.id,
            backendId: props.session.backendId,
          })
        }
      >
        Reload
      </button>
    </output>
  );
}
