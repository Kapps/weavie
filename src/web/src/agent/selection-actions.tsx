import { MessageSquare, Plus } from "lucide-solid";
import { createSignal, Show } from "solid-js";
import { render } from "solid-js/web";
import { type ClientSession, onSelectedSession } from "../bridge";
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

/** A captured excerpt belongs to its source and exact session, even while a command takes focus. */
export function createSelectionActions(host: HTMLElement, session: ClientSession) {
  let release: (() => void) | null = null;
  const clear = (): void => release?.();
  const show = (selection: AgentSelection, point: { x: number; y: number }): void => {
    releaseCurrent?.();
    if (session.closed || modalActive()) return;
    const above = point.y > window.innerHeight / 2;
    const inset = Math.max(8, above ? window.innerHeight - point.y + 24 : point.y + 4);
    const previousFocus = document.activeElement;
    const root = document.createElement("div");
    document.body.append(root);
    const [commenting, setCommenting] = createSignal(false);
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
      sendAgentSelection(session, selection, note());
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
            <textarea
              class="selection-comment-input ui-field"
              data-agent-selection-comment
              aria-label="Comment"
              placeholder="Comment"
              rows={3}
              ref={(element) => queueMicrotask(() => element.focus())}
              value={comment()}
              onInput={(event) => setComment(event.currentTarget.value)}
            />
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
                disabled={note() === ""}
                title={
                  evaluateWhen("compact")
                    ? "Send"
                    : title(CommandIds.sendAgentSelectionComment, "Send")
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
        !commenting() &&
        event.target instanceof Node &&
        (host.contains(event.target) || event.target.contains(host))
      )
        clear();
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
  const offSession = onSelectedSession((selected) => {
    if (selected !== session) clear();
  });
  session.signal.addEventListener("abort", clear);
  return {
    show,
    clear,
    dispose: () => {
      clear();
      offSession();
      offModal();
      session.signal.removeEventListener("abort", clear);
    },
  };
}
