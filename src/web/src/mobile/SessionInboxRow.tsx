import type { JSX } from "solid-js";
import { agentProviders } from "../chrome/agent-default";
import { type RailSession, STATUS_SHORT } from "../chrome/session-store";

/** One compact session target with its location, provider, and live/dormant state. */
export function SessionInboxRow(props: {
  session: RailSession;
  onOpen: (session: RailSession) => Promise<boolean>;
}): JSX.Element {
  const session = (): RailSession => props.session;
  return (
    <button
      type="button"
      class={`session-inbox-row status-${session().status}`}
      classList={{ active: session().active, offline: session().offline }}
      disabled={session().pending || session().offline}
      ref={(element) => element.style.setProperty("--chip-hue", String(session().hue))}
      onClick={() => void props.onOpen(session())}
    >
      <span class="session-inbox-monogram">{session().monogram}</span>
      <span class="session-inbox-details">
        <strong>{session().label}</strong>
        <span>
          {session().locationName} ·{" "}
          {agentProviders(session().backendId).find(
            (provider) => provider.id === session().providerId,
          )?.name ?? session().providerId}
        </span>
      </span>
      <span class="session-inbox-state">
        {session().loaded && <span class="session-status" />}
        {session().offline
          ? "Reconnecting"
          : session().loaded
            ? STATUS_SHORT[session().status]
            : "Unloaded"}
      </span>
    </button>
  );
}
