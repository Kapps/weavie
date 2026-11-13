import { type Accessor, createRoot, createSignal } from "solid-js";
import type { AgentPaneUpdate } from "../bridge";
import {
  type ActivityDetailsSource,
  createActivityDetailsSource,
} from "./AgentActivityDetailsSource";
import { type AgentActivitySegment, AgentActivitySegments } from "./AgentActivitySegments";
import type { ProjectedAgentActivity } from "./AgentPaneActivitySummary";
import { estimateEntrySize } from "./AgentPaneEstimate";
import { projectAgentTranscript } from "./AgentPaneMessages";
import { sideConversationEntry, subagentEntry } from "./AgentPaneSideConversations";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { orderPendingRequests } from "./AgentPendingRequests";
import { presentThoughts, type ThoughtDisplay } from "./AgentThoughtPresentation";
import { computeSectionLabels, latestAgentTurnStartId } from "./AgentTranscriptLabels";
import {
  cardIndexes,
  emptyTranscript,
  latestResultIndex,
  shareTranscript,
} from "./AgentTranscriptSnapshot";
import { createUpdatePresentation, type ProgressDisplay } from "./AgentUpdatePresentation";
import { activeTurnStartedAt, hasActiveTurn, pendingRequest } from "./turn-progress";

/** One projection over indexed rows. Nested cards hold this view, never a copied child history. */
export class AgentConversationProjection {
  private readonly state = createSignal(emptyTranscript);
  private readonly caption = createSignal<AgentTranscriptEntry | null>(null);
  private readonly projected = createSignal(undefined, { equals: false });
  private segments = new AgentActivitySegments();
  private activities = new Map<
    string,
    {
      index: number;
      projection: ProjectedAgentActivity;
      details: ActivityDetailsSource;
    }
  >();
  private readonly presentation;
  readonly transcript;
  readonly dispose: () => void;
  readonly card = this.caption[0];
  readonly pendingCards = new Set<string>();
  entryIndexes = new Map<string, number>();
  readonly linked: AgentTranscriptEntry;
  private identity: Required<Pick<AgentTranscriptEntry, "id" | "kind" | "conversationId">> | null =
    null;
  request: ReturnType<typeof pendingRequest> = null;
  active = false;
  estimatedHeight = 76;
  startedAt: number | null = null;

  constructor(
    private readonly expanded: Set<string>,
    private readonly thoughtDisplay: Accessor<ThoughtDisplay>,
    private readonly progressDisplay: Accessor<ProgressDisplay>,
  ) {
    this.presentation = createRoot((dispose) => ({
      ...createUpdatePresentation(this.state[0], progressDisplay, expanded),
      dispose,
    }));
    this.dispose = this.presentation.dispose;
    this.transcript = this.presentation.transcript;
    const view = this;
    this.linked = {
      conversation: view,
      get id() {
        return view.identity!.id;
      },
      get kind() {
        return view.identity!.kind;
      },
      get conversationId() {
        return view.identity!.conversationId;
      },
      get actionMessage() {
        return view.card()!.actionMessage;
      },
      get detailCount() {
        return view.card()!.detailCount;
      },
      get details() {
        return view.card()!.details;
      },
      get label() {
        return view.card()!.label;
      },
      get status() {
        return view.card()!.status;
      },
      get streaming() {
        return view.card()!.streaming;
      },
      get summary() {
        return view.card()!.summary;
      },
      get text() {
        return view.card()!.text;
      },
      get tone() {
        return view.card()!.tone;
      },
      get asideActive() {
        return view.card()?.asideActive;
      },
      get asideCollapsed() {
        return view.card()?.asideCollapsed;
      },
      get asideReplyable() {
        return view.card()?.asideReplyable;
      },
      get subagent() {
        return view.card()?.subagent;
      },
      get currentThought() {
        return view.transcript().currentThought;
      },
      get asideEntries() {
        return view.transcript().entries;
      },
      get pendingEntries() {
        const snapshot = view.transcript();
        return snapshot.pendingRowIndexes.map((index) => snapshot.entries[index]!);
      },
    };
  }

  bind(marker: AgentPaneUpdate | null): void {
    if (marker === null || this.identity !== null) return;
    const kind = marker.type === "subagent-started" ? "subagent" : "aside";
    this.identity = {
      id: `${kind}-${marker.conversationId}`,
      kind,
      conversationId: marker.conversationId!,
    };
  }

  publish(
    rows: AgentPaneUpdate[],
    changes: AgentPaneUpdate[],
    cards: ReadonlyMap<AgentPaneUpdate, AgentTranscriptEntry>,
    parentName: string | null,
    marker: AgentPaneUpdate | null,
  ): void {
    const active = hasActiveTurn(rows);
    const changedState = active !== this.active;
    this.active = active;
    this.startedAt = activeTurnStartedAt(rows);
    this.request = pendingRequest(rows);
    const project = (messages: readonly AgentPaneUpdate[]): AgentTranscriptEntry[] => {
      if (!changedState && this.upsert(changes)) return [];
      const projection = projectAgentTranscript(messages, cards, this.thoughtDisplay());
      this.segments = projection.segments;
      const presented = presentThoughts(projection.entries, this.thoughtDisplay(), this.expanded);
      const turnStartId = latestAgentTurnStartId(presented);
      const ordered = orderPendingRequests(presented);
      const previousActivities = this.activities;
      this.activities = new Map();
      this.entryIndexes = new Map();
      ordered.entries.forEach((entry, index) => {
        this.entryIndexes.set(entry.id, index);
        const activity = projection.segments.groups.get(entry.id)?.projection;
        if (activity !== undefined) {
          const details =
            previousActivities.get(entry.id)?.details ?? createActivityDetailsSource(activity);
          details.replace(activity);
          entry.activityDetails = details;
          entry.activityRevision = details.revision;
          this.activities.set(entry.id, { index, projection: activity, details });
        }
      });
      this.state[1]((previous) =>
        shareTranscript(previous, {
          agentTurnStartId: turnStartId,
          agentTurnStartIndex:
            turnStartId === null ? null : (this.entryIndexes.get(turnStartId) ?? null),
          currentThought: this.thoughtDisplay() === "current" ? projection.currentThought : null,
          entries: ordered.entries,
          entryIndexes: this.entryIndexes,
          cardIndexes: cardIndexes(ordered.entries),
          latestResultIndex: latestResultIndex(ordered.entries),
          pendingRowIndexes: ordered.pendingRowIndexes,
          sectionLabels: computeSectionLabels(ordered.entries, active),
          turnActive: active,
        }),
      );
      return [];
    };
    if (marker?.type === "subagent-started")
      this.caption[1](subagentEntry(rows, parentName, project));
    else if (marker?.type === "side-conversation-started")
      this.caption[1](sideConversationEntry(marker, rows, project));
    else {
      project(rows);
      this.caption[1](null);
    }
    this.measure();
    this.projected[1]();
  }

  measure(): void {
    const snapshot = this.transcript();
    const card = this.card();
    const entries = card?.asideCollapsed
      ? snapshot.pendingRowIndexes.map((index) => snapshot.entries[index]!)
      : snapshot.entries;
    this.estimatedHeight =
      76 +
      (snapshot.currentThought !== null && !card?.asideCollapsed ? 24 : 0) +
      entries.reduce((height, entry) => height + estimateEntrySize(entry), 0);
  }

  childResized(id: string, delta: number): void {
    if (this.progressDisplay() === "latest") this.measure();
    else if (this.card()?.asideCollapsed !== true || this.pendingCards.has(id))
      this.estimatedHeight += delta;
  }

  /** Retain only the indexed cards with pending forms; their content updates independently. */
  setCardPending(id: string, pending: boolean): void {
    if (this.pendingCards.has(id) === pending) return;
    if (pending) this.pendingCards.add(id);
    else this.pendingCards.delete(id);
    this.state[1]((previous) => {
      const indexes = new Set(
        previous.pendingRowIndexes.filter((index) => previous.entries[index]?.kind === "request"),
      );
      for (const key of this.pendingCards) {
        const index = this.entryIndexes.get(key);
        if (index !== undefined) indexes.add(index);
      }
      return { ...previous, pendingRowIndexes: [...indexes].sort((a, b) => a - b) };
    });
    if (this.progressDisplay() === "latest" || this.card()?.asideCollapsed) this.measure();
  }

  activityStep(id: string) {
    this.projected[0]();
    for (const activity of this.activities.values()) {
      const step = activity.projection.step(id);
      if (step !== undefined) return step;
    }
    return undefined;
  }

  setExpanded(id: string, expanded: boolean): boolean {
    if (this.presentation.setExpanded(id, expanded)) {
      this.measure();
      return true;
    }
    const index = this.entryIndexes.get(id);
    const thought =
      index !== undefined && this.state[0]().entries[index]?.thoughtCollapsed === true;
    if (!thought) return false;
    if (expanded) this.expanded.add(id);
    else this.expanded.delete(id);
    this.updateEntry({ index: index! }, (entry) => ({ ...entry, thoughtOpen: expanded }));
    this.measure();
    return true;
  }

  private upsert(changes: AgentPaneUpdate[]): boolean {
    if (changes.length === 0) return false;
    const mutations: Array<{
      activity: {
        index: number;
        projection: ProjectedAgentActivity;
        details: ActivityDetailsSource;
      };
      message: AgentPaneUpdate;
      segment: AgentActivitySegment;
    }> = [];
    for (const message of changes) {
      if (!message.turnId) return false;
      const segment = this.segments.find(message, "");
      const activity = segment === undefined ? undefined : this.activities.get(segment.entry.id);
      if (
        segment === undefined ||
        activity === undefined ||
        !activity.projection.canUpsert(message)
      )
        return false;
      mutations.push({ activity, segment, message });
    }
    const changed = new Map<(typeof mutations)[number]["activity"], Set<number>>();
    for (const { activity, segment, message } of mutations) {
      const indexes = changed.get(activity) ?? new Set<number>();
      indexes.add(this.segments.upsert(segment, message));
      changed.set(activity, indexes);
    }
    for (const [activity, indexes] of changed) {
      activity.details.update(indexes);
      this.updateEntry(activity, (entry) => {
        return {
          ...entry,
          ...activity.projection.summary(),
          detailCount: activity.projection.count,
          activityRevision: activity.details.revision,
        };
      });
    }
    return true;
  }

  private updateEntry(
    activity: { index: number },
    update: (entry: AgentTranscriptEntry) => AgentTranscriptEntry,
  ): void {
    this.state[1]((previous) => {
      const entries = [...previous.entries];
      entries[activity.index] = update(entries[activity.index]!);
      return { ...previous, entries };
    });
  }
}
