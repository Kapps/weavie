import { MessageSquare, Plus } from "lucide-solid";
import { createSignal, Show } from "solid-js";
import { render } from "solid-js/web";
import { type ClientSession, registerViewFeature } from "../bridge";
import { modalActive, onModalOpened } from "../chrome/modal-state";
import { insideOmnibar } from "../chrome/omnibar-controller";
import { evaluateWhen, setContext } from "../commands/context";
import { keyHintInCatalog } from "../commands/key-hint";
import { type CommandHandler, dispatchCommand, registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import {
  type AgentSelection,
  attachAgentSelection,
  commentedSelection,
  sendAgentSelection,
} from "./composer-store";
import "./selection-actions.css";

let releaseCurrent: (() => void) | null = null;

/** Where Send takes a selection and its comment; returns the submission id, or null when nothing was sent. */
export type SelectionSend = (
  session: ClientSession,
  selection: AgentSelection,
  comment: string,
) => string | null;

/** Send to the session's main conversation, as a prompt of its own. */
export const sendToConversation: SelectionSend = (session, selection, comment) =>
  sendAgentSelection(session, selection, comment);

/** The selection toolbar of one surface. */
export type SelectionActions = ReturnType<typeof createSelectionActions>;

/**
 * A captured excerpt belongs to its source and exact session, even while a command takes focus. `blocked` says why
 * Send can't go where `deliver` takes it, or null when it can.
 */
export function createSelectionActions(
  host: HTMLElement,
  session: ClientSession,
  deliver: SelectionSend,
  blocked: () => string | null,
) {
  let release: (() => void) | null = null;
  let commentOpen = (): boolean => false;
  const clear = (): void => release?.();
  /** The selection moved on screen: a toolbar would sit off it, while a comment being written stays. */
  const moved = (): void => {
    if (!commentOpen()) clear();
  };
  /** `sent` receives the submission id and comment of a selection sent as its own prompt. */
  const show = (
    selection: AgentSelection,
    point: { x: number; y: number },
    sent: (submissionId: string, comment: string) => void,
  ): void => {
    releaseCurrent?.();
    if (session.closed || modalActive()) return;
    const above = point.y > window.innerHeight / 2;
    const inset = Math.max(8, above ? window.innerHeight - point.y + 24 : point.y + 4);
    const previousFocus = document.activeElement;
    const root = document.createElement("div");
    document.body.append(root);
    const [commenting, setCommenting] = createSignal(false);
    commentOpen = commenting;
    const [comment, setComment] = createSignal("");
    const note = (): string => comment().trim();
    const attach = (): boolean => {
      attachAgentSelection(
        session,
        note() === "" ? selection : commentedSelection(selection, note()),
      );
      clear();
      return true;
    };
    const send = (): boolean => {
      if (blocked() !== null) return true;
      const id = deliver(session, selection, note());
      if (id !== null) sent(id, note());
      clear();
      return true;
    };
    // An empty note still consumes the key, so Enter neither types a newline nor reaches another binding.
    const commented = (act: () => boolean) => (): boolean =>
      commenting() && (note() === "" || act());
    const owned =
      (run: () => boolean): CommandHandler =>
      (_args, context) =>
        context.session === session && run();
    const offCommands = [
      registerCommand(CommandIds.attachAgentSelection, owned(attach)),
      registerCommand(
        CommandIds.commentAgentSelection,
        owned(() => setCommenting(true)),
      ),
      registerCommand(CommandIds.sendAgentSelectionComment, owned(commented(send))),
      registerCommand(CommandIds.addAgentSelectionComment, owned(commented(attach))),
    ];
    setContext("agentSelectionAttachable", true);
    const title = (id: string, label: string): string =>
      label + keyHintInCatalog(session.connection.id, id);
    const unmount = render(
      () => (
        <Show
          when={commenting()}
          fallback={
            <div
              class="selection-actions"
              role="toolbar"
              aria-label="Selection actions"
              style={`left:${Math.max(8, Math.min(point.x, window.innerWidth - 88))}px;top:${Math.max(8, Math.min(point.y + 4, window.innerHeight - 44))}px`}
              onPointerDown={(event) => event.preventDefault()}
            >
              <button
                type="button"
                class="ui-icon-button"
                aria-label="Comment on Selection"
                title={title(CommandIds.commentAgentSelection, "Comment on Selection")}
                onClick={() => void dispatchCommand(CommandIds.commentAgentSelection)}
              >
                <MessageSquare aria-hidden="true" />
              </button>
              <button
                type="button"
                class="ui-icon-button"
                aria-label="Attach Selection to Prompt"
                title={title(CommandIds.attachAgentSelection, "Attach Selection to Prompt")}
                onClick={() => void dispatchCommand(CommandIds.attachAgentSelection)}
              >
                <Plus aria-hidden="true" />
              </button>
            </div>
          }
        >
          <section
            class="selection-comment"
            data-comment-box
            role="dialog"
            aria-label="Comment on Selection"
            style={`left:clamp(8px, ${point.x}px, calc(100vw - 368px));${above ? "bottom" : "top"}:${inset}px;max-height:calc(100vh - ${inset + 8}px)`}
          >
            <label class="ui-draft ui-field ui-draft-resize">
              <textarea
                data-agent-selection-comment
                aria-label="Comment"
                placeholder="Comment"
                rows={3}
                ref={(element) => queueMicrotask(() => element.focus())}
                value={comment()}
                onInput={(event) => setComment(event.currentTarget.value)}
              />
            </label>
            <div class="selection-comment-actions">
              <button type="button" class="ui-button" title="Cancel (Esc)" onClick={clear}>
                Cancel
              </button>
              <button
                type="button"
                class="ui-button"
                disabled={note() === ""}
                title={title(CommandIds.addAgentSelectionComment, "Add to context")}
                onClick={attach}
              >
                Add to context
              </button>
              <button
                type="button"
                class="ui-button ui-button-primary"
                disabled={note() === "" || blocked() !== null}
                title={
                  blocked() ??
                  (evaluateWhen("compact")
                    ? "Send"
                    : title(CommandIds.sendAgentSelectionComment, "Send"))
                }
                onClick={send}
              >
                Send
              </button>
            </div>
          </section>
        </Show>
      ),
      root,
    );
    const onPress = (event: Event): void => {
      if (root.contains(event.target as Node) || insideOmnibar(event.target as Node)) return;
      clear();
    };
    const onScroll = (event: Event): void => {
      if (
        event.target instanceof Node &&
        (host.contains(event.target) || event.target.contains(host))
      )
        moved();
    };
    const onKey = (event: KeyboardEvent): void => {
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        clear();
      }
    };
    release = () => {
      if (releaseCurrent === release) releaseCurrent = null;
      release = null;
      commentOpen = () => false;
      for (const off of offCommands) off();
      setContext("agentSelectionAttachable", false);
      document.removeEventListener("pointerdown", onPress, true);
      document.removeEventListener("scroll", onScroll, true);
      document.removeEventListener("keydown", onKey);
      const restoreFocus = root.contains(document.activeElement);
      unmount();
      root.remove();
      if (restoreFocus && previousFocus instanceof HTMLElement && previousFocus.isConnected)
        previousFocus.focus({ preventScroll: true });
    };
    releaseCurrent = release;
    document.addEventListener("pointerdown", onPress, true);
    document.addEventListener("scroll", onScroll, true);
    document.addEventListener("keydown", onKey);
  };
  const offModal = onModalOpened(clear);
  const offSession = registerViewFeature((shown) => (shown === session ? clear : undefined));
  session.signal.addEventListener("abort", clear);
  return {
    show,
    clear,
    moved,
    dispose: () => {
      clear();
      offSession();
      offModal();
      session.signal.removeEventListener("abort", clear);
    },
  };
}
