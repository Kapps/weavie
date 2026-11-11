import type { AgentPaneUpdate } from "../bridge";

export interface ConversationRows {
  readonly key: string;
  readonly indexes: number[];
  readonly children: Set<ConversationRows>;
  readonly cards: Set<ConversationRows>;
  marker: AgentPaneUpdate | null;
  markerIndex: number;
  parent: ConversationRows | null;
  container: ConversationRows | null;
}

export function conversationKey(message: AgentPaneUpdate): string {
  return message.conversationId ? `${message.providerId}\0${message.conversationId}` : "";
}

/** Membership indexes over the accumulator's single row store. */
export class AgentPaneConversationRows {
  readonly conversations = new Map<string, ConversationRows>();
  readonly placements = new Set<ConversationRows>();

  constructor(readonly messages: AgentPaneUpdate[]) {
    this.get("");
  }

  get(key: string): ConversationRows {
    let scope = this.conversations.get(key);
    if (scope === undefined) {
      scope = {
        key,
        indexes: [],
        children: new Set(),
        cards: new Set(),
        marker: null,
        markerIndex: -1,
        parent: null,
        container: null,
      };
      this.conversations.set(key, scope);
    }
    return scope;
  }

  register(index: number): void {
    const message = this.messages[index]!;
    const scope = this.get(conversationKey(message));
    scope.indexes.push(index);
    if (message.type !== "subagent-started" && message.type !== "side-conversation-started") return;
    if (scope.marker !== null) return;
    scope.marker = message;
    scope.markerIndex = index;
    scope.parent = this.get(
      message.parentItemId ? `${message.providerId}\0${message.parentItemId}` : "",
    );
    scope.parent.children.add(scope);
    this.place(scope);
  }

  /** Called only when membership is announced, including announcements arriving after descendants. */
  private place(scope: ConversationRows): void {
    const parent = scope.parent;
    const container =
      parent?.key === "" || parent?.marker?.type === "side-conversation-started"
        ? parent
        : (parent?.container ?? null);
    if (scope.container !== container) {
      this.placements.add(scope);
      if (scope.container !== null) {
        scope.container.cards.delete(scope);
        this.placements.add(scope.container);
      }
      scope.container = container;
      if (container !== null) {
        container.cards.add(scope);
        this.placements.add(container);
      }
    }
    for (const child of scope.children) this.place(child);
  }

  rows(scope: ConversationRows): AgentPaneUpdate[] {
    return scope.indexes.map((index) => this.messages[index]!);
  }
}
