import { createEffect, createSignal, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentCardReply } from "./AgentCardReply";
import type { ReplyComposer } from "./composer-store";

/** A card's Reply button and box; the box takes focus only where Reply was pressed, so a second view never steals it. */
export function AgentCardReplyBox(props: {
  session: ClientSession;
  composer: ReplyComposer;
  /** What the box's input is called: "Reply to BTW". */
  label: string;
  hint: string;
  /** The Reply button's tooltip: its command and shortcut where it has one. */
  replyTitle: string;
  disabled: boolean;
}): JSX.Element {
  // Read where it is used: its host hands over a new one when the transcript reloads.
  const composer = () => props.composer;
  const [openedHere, setOpenedHere] = createSignal(false);
  // However the box closes (Cancel, a sent reply, the other view), the next Reply starts unclaimed.
  createEffect(() => {
    if (!composer().state().replyOpen) setOpenedHere(false);
  });
  return (
    <Show
      when={composer().state().replyOpen}
      fallback={
        <button
          type="button"
          class="agent-aside-reply-button ui-button"
          disabled={props.disabled}
          title={props.replyTitle}
          onClick={() => {
            setOpenedHere(true);
            composer().setOpen(true);
          }}
        >
          Reply
        </button>
      }
    >
      <AgentCardReply
        session={props.session}
        composer={composer()}
        label={props.label}
        hint={props.hint}
        autofocus={openedHere()}
        onClose={() => composer().setOpen(false)}
      />
    </Show>
  );
}
