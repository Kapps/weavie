import { createSignal, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentAsideReply } from "./AgentAsideReply";
import { replyComposer } from "./composer-store";

/** A BTW's Reply button and box; the box takes focus only where Reply was pressed, so a second view never steals it. */
export function AgentAsideReplyBox(props: {
  session: ClientSession;
  conversationId: string;
  disabled: boolean;
}): JSX.Element {
  const composer = replyComposer(props.session, props.conversationId);
  const [openedHere, setOpenedHere] = createSignal(false);
  const close = (): void => {
    setOpenedHere(false);
    composer.setOpen(false);
  };
  return (
    <Show
      when={composer.state().replyOpen}
      fallback={
        <button
          type="button"
          class="agent-aside-reply-button ui-button ui-button-content"
          disabled={props.disabled}
          onClick={() => {
            setOpenedHere(true);
            composer.setOpen(true);
          }}
        >
          Reply
        </button>
      }
    >
      <AgentAsideReply
        session={props.session}
        conversationId={props.conversationId}
        autofocus={openedHere()}
        onClose={close}
      />
    </Show>
  );
}
