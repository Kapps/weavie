import { EllipsisVertical } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import { agentProviders } from "../chrome/agent-default";
import type { RailSession } from "../chrome/session-store";
import { sessionSubtitle, statusDot } from "../chrome/session-summary";
import { useNow } from "../compact-age";

/**
 * One compact session target with its location, provider, and live/dormant state. Touch chrome hides the rail,
 * so the row also carries the session menu: its actions button here, and the hold gesture the list owns —
 * which finds the row by the identity in these data attributes.
 */
export function SessionInboxRow(props: {
  session: RailSession;
  compact: boolean;
  onOpen: (session: RailSession) => Promise<boolean>;
  onManage: (session: RailSession, x: number, y: number) => void;
}): JSX.Element {
  const session = (): RailSession => props.session;
  const manageLabel = (): string => `Manage ${session().label}`;
  const now = useNow();
  return (
    <div
      class="session-inbox-row"
      classList={{ active: session().active, offline: session().offline }}
      data-session-id={session().id}
      data-backend-id={session().backendId}
    >
      <button
        type="button"
        class="session-inbox-open"
        disabled={session().pending || session().offline}
        onClick={() => void props.onOpen(session())}
      >
        <span class={`session-status status-${statusDot(session())}`} />
        <span class="session-inbox-details">
          <strong>
            <span class="session-inbox-title">{session().title}</span>
            <Show when={session().prefix}>
              {(prefix) => <span class="session-inbox-prefix">{prefix()}</span>}
            </Show>
          </strong>
          <span class="session-inbox-status">{sessionSubtitle(session(), now())}</span>
        </span>
        <span class="session-inbox-where">
          {session().locationName} ·{" "}
          {agentProviders(session().backendId).find(
            (provider) => provider.id === session().providerId,
          )?.name ?? session().providerId}
        </span>
      </button>
      <Show when={props.compact}>
        <button
          type="button"
          class="session-inbox-manage"
          aria-label={manageLabel()}
          title={manageLabel()}
          onClick={(event) => {
            const bounds = event.currentTarget.getBoundingClientRect();
            props.onManage(session(), bounds.right, bounds.bottom);
          }}
        >
          <EllipsisVertical size={18} aria-hidden="true" />
        </button>
      </Show>
    </div>
  );
}
