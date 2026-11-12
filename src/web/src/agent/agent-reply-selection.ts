import type { ClientSession } from "../bridge";
import { agentAttachOnSelect } from "../chrome/agent-default";
import { setContext } from "../commands/context";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { attachAgentSelection } from "./composer-store";

const LABEL_LENGTH = 60;
const replies = new WeakMap<Element, ClientSession>();
// The reply whose text was last highlighted owns Attach Selection to Prompt, with that text, until the next press
// in a reply: opening the palette to run the command collapses the highlight but must not lose it.
let owner: { host: Element; text: string; release: () => void } | null = null;
let lastAttached: { host: Element; text: string } | null = null;
let tracking = false;

/** Lets highlighted text in one agent reply be attached to its session's next prompt. */
export function installReplySelection(host: HTMLElement, session: ClientSession): () => void {
  if (!tracking) {
    tracking = true;
    document.addEventListener("selectionchange", trackOwner);
  }
  replies.set(host, session);
  const attachOnRelease = (): void => {
    if (agentAttachOnSelect(session.connection.id)) setTimeout(() => attachCurrent(host, session));
  };
  const disown = (): void => {
    if (owner !== null) release();
  };
  host.addEventListener("pointerdown", disown);
  host.addEventListener("pointerup", attachOnRelease);
  return () => {
    host.removeEventListener("pointerdown", disown);
    host.removeEventListener("pointerup", attachOnRelease);
    replies.delete(host);
    if (owner?.host === host) release();
  };
}

function trackOwner(): void {
  if (document.getSelection()?.isCollapsed !== false) lastAttached = null;
  const host = selectedReply();
  const session = host === null ? undefined : replies.get(host);
  if (host === null || session === undefined) return;
  const text = selectedText();
  if (owner?.host === host) {
    owner.text = text;
    return;
  }
  if (owner !== null) release();
  const off = registerCommand(CommandIds.attachAgentSelection, () => {
    if (owner === null || owner.text.length === 0) return false;
    attachAgentSelection(session, { label: selectionLabel(owner.text), text: owner.text });
  });
  setContext("agentSelectionAttachable", true);
  owner = {
    host,
    text,
    release: () => {
      off();
      setContext("agentSelectionAttachable", false);
    },
  };
}

function release(): void {
  owner?.release();
  owner = null;
}

// The reply that contains the whole non-empty selection, when one does.
function selectedReply(): Element | null {
  const selection = document.getSelection();
  if (selection === null || selection.isCollapsed) return null;
  const element = (node: Node | null): Element | null =>
    node instanceof Element ? node : (node?.parentElement ?? null);
  const host = element(selection.anchorNode)?.closest(".agent-markdown") ?? null;
  return host !== null && replies.has(host) && host.contains(selection.focusNode) ? host : null;
}

function selectedText(): string {
  return document.getSelection()?.toString().trim() ?? "";
}

// Releasing the pointer again over the same highlight is not a new selection.
function attachCurrent(host: Element, session: ClientSession): void {
  const text = selectedText();
  if (selectedReply() !== host || text.length === 0) return;
  if (lastAttached?.host === host && lastAttached.text === text) return;
  lastAttached = { host, text };
  attachAgentSelection(session, { label: selectionLabel(text), text });
}

/** A one-line chip label for selected text. */
export function selectionLabel(text: string): string {
  const line = text.replace(/\s+/g, " ").trim();
  return line.length <= LABEL_LENGTH ? line : `${line.slice(0, LABEL_LENGTH - 1)}…`;
}
