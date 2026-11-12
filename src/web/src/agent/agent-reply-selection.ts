import type { ClientSession } from "../bridge";
import { agentAttachOnSelect } from "../chrome/agent-default";
import { setContext } from "../commands/context";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { attachAgentSelection } from "./composer-store";

const LABEL_LENGTH = 60;
const replies = new WeakMap<Element, ClientSession>();
// The reply holding the document's selection owns Attach Selection to Prompt while it does.
let owner: { host: Element; release: () => void } | null = null;
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
    if (agentAttachOnSelect(session.connection.id)) setTimeout(() => attach(host, session, false));
  };
  host.addEventListener("pointerup", attachOnRelease);
  return () => {
    host.removeEventListener("pointerup", attachOnRelease);
    replies.delete(host);
    if (owner?.host === host) {
      owner.release();
      owner = null;
    }
  };
}

function trackOwner(): void {
  const host = selectedReply();
  if (host === owner?.host) return;
  owner?.release();
  owner = null;
  const session = host === null ? undefined : replies.get(host);
  if (host === null || session === undefined) return;
  const off = registerCommand(CommandIds.attachAgentSelection, () => attach(host, session, true));
  setContext("agentSelectionAttachable", true);
  owner = {
    host,
    release: () => {
      off();
      setContext("agentSelectionAttachable", false);
    },
  };
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

function attach(host: Element, session: ClientSession, explicit: boolean): boolean {
  if (selectedReply() !== host) return false;
  const text = document.getSelection()?.toString().trim() ?? "";
  if (text.length === 0) return false;
  // Releasing the pointer again over the same highlight is not a new selection.
  if (!explicit && lastAttached?.host === host && lastAttached.text === text) return false;
  lastAttached = { host, text };
  attachAgentSelection(session, { label: selectionLabel(text), text });
  return true;
}

/** A one-line chip label for selected text. */
export function selectionLabel(text: string): string {
  const line = text.replace(/\s+/g, " ").trim();
  return line.length <= LABEL_LENGTH ? line : `${line.slice(0, LABEL_LENGTH - 1)}…`;
}
