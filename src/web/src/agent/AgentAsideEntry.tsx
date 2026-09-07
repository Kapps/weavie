import { createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import { newestVisibleAgentElement } from "./AgentViewport";
import { asideReplyState, setAsideReplyState } from "./aside-reply-store";

const toggles = new Map<HTMLElement, () => void>();

export function toggleAgentAside(): boolean {
  const active = document.querySelector(".agent-surface.active");
  const focused = document.activeElement?.closest<HTMLElement>(".agent-aside");
  const target =
    focused && active?.contains(focused) ? focused : newestVisibleAgentElement(".agent-aside");
  const toggle = target === undefined ? undefined : toggles.get(target);
  if (toggle === undefined) return false;
  toggle();
  return true;
}

export function AsideEntry(props: {
  entry: AgentTranscriptEntry;
  expandedDetails: ReadonlySet<string>;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  keyboardApprovalId: string | null;
  keyboardInputId: string | null;
  session: ClientSession;
}): JSX.Element {
  const conversationId = props.entry.conversationId;
  if (typeof conversationId !== "string" || conversationId.length === 0) {
    throw new Error("An aside entry requires a conversation id.");
  }
  const saved = asideReplyState(props.session, conversationId);
  const [replying, setReplying] = createSignal(saved.open);
  const [draft, setDraft] = createSignal(saved.draft);
  let card: HTMLElement | undefined;
  const collapsed = () => props.expandedDetails.has(props.entry.id);
  const toggle = () => props.onDetailsToggle(props.entry.id, !collapsed());
  const toggleLabel = () => (collapsed() ? "Expand BTW" : "Collapse BTW");
  const toggleTitle = () => {
    const key = liveKeyLabel(CommandIds.toggleAgentAside);
    return key === "" ? toggleLabel() : `${toggleLabel()} (${key})`;
  };
  onMount(() => {
    toggles.set(card!, toggle);
    onCleanup(() => toggles.delete(card!));
  });
  let textarea: HTMLTextAreaElement | undefined;

  const updateDraft = (value: string): void => {
    setDraft(value);
    setAsideReplyState(props.session, conversationId, { draft: value, open: replying() });
  };

  const setReplyOpen = (open: boolean): void => {
    setReplying(open);
    setAsideReplyState(props.session, conversationId, { draft: draft(), open });
  };

  const submit = (): void => {
    const prompt = draft().trim();
    if (prompt.length === 0) return;
    props.session.feature("agent").publish("replyAside", {
      conversationId,
      prompt,
    });
    updateDraft("");
    setReplyOpen(false);
  };

  return (
    <article ref={card} class="agent-aside" data-agent-aside={props.entry.conversationId}>
      <button
        type="button"
        class="agent-aside-head"
        aria-label={toggleLabel()}
        aria-expanded={!collapsed()}
        title={toggleTitle()}
        onClick={toggle}
      >
        <span>{collapsed() ? "▸" : "▾"} BTW</span>
        <Show when={props.entry.status !== null}>
          <small>{props.entry.status}</small>
        </Show>
      </button>
      <div hidden={collapsed()}>
        <div class="agent-aside-transcript">
          <For each={props.entry.asideEntries ?? []}>
            {(entry) => (
              <TranscriptEntry
                expandedDetails={props.expandedDetails}
                entry={entry}
                keyboardApprovalId={props.keyboardApprovalId}
                keyboardInputId={props.keyboardInputId}
                onDetailsToggle={props.onDetailsToggle}
                sectionLabel={null}
                session={props.session}
              />
            )}
          </For>
        </div>
        <Show when={props.entry.asideReplyable !== false}>
          <Show
            when={replying()}
            fallback={
              <button
                type="button"
                class="agent-aside-reply-button"
                disabled={props.entry.asideActive === true}
                onClick={() => {
                  setReplyOpen(true);
                  queueMicrotask(() => textarea?.focus());
                }}
              >
                Reply
              </button>
            }
          >
            <div class="agent-aside-reply">
              <textarea
                ref={textarea}
                aria-label="Reply to BTW"
                rows={2}
                value={draft()}
                onInput={(event) => updateDraft(event.currentTarget.value)}
                onKeyDown={(event) => {
                  if (event.key === "Escape") {
                    event.preventDefault();
                    setReplyOpen(false);
                  } else if (event.key === "Enter" && !event.shiftKey) {
                    event.preventDefault();
                    submit();
                  }
                }}
              />
              <div class="agent-aside-reply-actions">
                <button type="button" onClick={() => setReplyOpen(false)}>
                  Cancel
                </button>
                <button type="button" disabled={draft().trim().length === 0} onClick={submit}>
                  Reply
                </button>
              </div>
            </div>
          </Show>
        </Show>
      </div>
    </article>
  );
}
