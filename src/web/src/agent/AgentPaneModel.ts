import {
  type Accessor,
  batch,
  createEffect,
  createMemo,
  createSignal,
  on,
  onCleanup,
} from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentConversationProjection } from "./AgentConversationProjection";
import { clearAgentInputDrafts } from "./AgentInputDrafts";
import {
  AgentPaneConversationRows,
  type ConversationRows,
  conversationKey,
} from "./AgentPaneConversationRows";
import type {
  AgentActivityStep,
  AgentTranscriptEntry,
  AgentTranscriptSnapshot,
} from "./AgentPaneTranscriptTypes";
import type { ThoughtDisplay } from "./AgentThoughtPresentation";
import type { ProgressDisplay } from "./AgentUpdatePresentation";
import { type AgentPlanIdentity, latestCompletedPlan } from "./agent-plan";
import { submittedPrompts } from "./prompt-history";
import type { PendingRequest, PendingRequestKind } from "./turn-progress";

export type { AgentSectionLabel } from "./AgentPaneTranscriptTypes";

export interface AgentPaneModel {
  readonly cardChanges: Accessor<readonly { id: string; delta: number }[]>;
  readonly generation: Accessor<number>;
  readonly history: Accessor<readonly string[]>;
  /** Whether the host's stored transcript has fully loaded. */
  readonly historyComplete: Accessor<boolean>;
  readonly interruptible: Accessor<boolean>;
  readonly keyboardApprovalId: Accessor<string | null>;
  readonly keyboardRequestKey: Accessor<string | null>;
  /** Counts published changes to the latest row, the output a reader away from the bottom hasn't seen. */
  readonly latestOutput: Accessor<number>;
  readonly latestPlan: Accessor<AgentPlanIdentity | null>;
  /** Whether the transcript is receiving live output rather than only history. */
  readonly live: Accessor<boolean>;
  /** The latest reply's id when a live message delivered it rather than the loaded history. */
  readonly liveReplyId: Accessor<string | null>;
  readonly pendingLegacyImageCount: Accessor<number>;
  readonly pendingRequestKind: Accessor<PendingRequestKind | null>;
  readonly session: ClientSession;
  readonly transcript: Accessor<AgentTranscriptSnapshot>;
  readonly turnActive: Accessor<boolean>;
  readonly turnStartedAt: Accessor<number | null>;
  conversation(id: string): AgentTranscriptEntry | undefined;
  setActivityExpanded(id: string, expanded: boolean): void;
  /** The live activity step with transcript item id `itemId`, in the transcript or a nested one. */
  activityStep(itemId: string): AgentActivityStep | undefined;
}

export interface MutableAgentPaneModel extends AgentPaneModel {
  /** Applies live output; `changes` are the messages it changed, or none to rebuild every conversation. */
  publish(rows: AgentPaneConversationRows, changes: AgentPaneUpdate[]): void;
  /** Applies the loaded history. */
  load(rows: AgentPaneConversationRows): void;
  setHistoryComplete(complete: boolean): void;
  /** Marks the transcript as receiving live output, which history loading never clears. */
  noteLive(): void;
  reset(): void;
}

export function createAgentPaneModel(
  session: ClientSession,
  thoughtDisplay: Accessor<ThoughtDisplay>,
  progressDisplay: Accessor<ProgressDisplay>,
): MutableAgentPaneModel {
  const [cardChanges, setCardChanges] = createSignal<readonly { id: string; delta: number }[]>([]);
  const [generation, setGeneration] = createSignal(0);
  const [latestOutput, setLatestOutput] = createSignal(0);
  const [history, setHistory] = createSignal<readonly string[]>([]);
  const [historyComplete, setHistoryComplete] = createSignal(false);
  const [live, setLive] = createSignal(false);
  const [liveReplyId, setLiveReplyId] = createSignal<string | null>(null);
  let latestReplyId: string | null = null;
  const [latestPlan, setLatestPlan] = createSignal<AgentPlanIdentity | null>(null);
  const [pendingLegacyImageCount, setPendingLegacyImageCount] = createSignal(0);
  const [request, setRequest] = createSignal<PendingRequest | null>(null);
  const [interruptible, setInterruptible] = createSignal(false);
  const [turnStartedAt, setTurnStartedAt] = createSignal<number | null>(null);
  const expanded = new Set<string>();
  const main = new AgentConversationProjection(expanded, thoughtDisplay, progressDisplay);
  const views = new Map<string, AgentConversationProjection>([["", main]]);
  onCleanup(() => {
    for (const view of views.values()) view.dispose();
  });
  const byId = new Map<
    string,
    ReturnType<typeof createSignal<AgentConversationProjection | undefined>>
  >();
  const membership = (id: string) => {
    let cell = byId.get(id);
    if (cell === undefined) {
      cell = createSignal<AgentConversationProjection>();
      byId.set(id, cell);
    }
    return cell;
  };
  const pending = new Map<string, { request: PendingRequest; index: number }>();
  const active = new Set<string>();
  let source = new AgentPaneConversationRows([]);
  let outputChanged = false;
  let pendingChanged = false;
  let resized = new Map<string, number>();

  const viewFor = (scope: ConversationRows): AgentConversationProjection => {
    let view = views.get(scope.key);
    if (view === undefined) {
      view = new AgentConversationProjection(expanded, thoughtDisplay, progressDisplay);
      views.set(scope.key, view);
    }
    view.bind(scope.marker);
    if (scope.marker?.conversationId) membership(scope.marker.conversationId)[1](view);
    return view;
  };

  const project = (scope: ConversationRows, changes: AgentPaneUpdate[]): void => {
    const view = viewFor(scope);
    const cards = new Map<AgentPaneUpdate, AgentTranscriptEntry>();
    const indexes = [...scope.indexes];
    for (const child of scope.cards) {
      cards.set(child.marker!, viewFor(child).linked);
      indexes.push(child.markerIndex);
    }
    const rows =
      scope.cards.size === 0
        ? source.rows(scope)
        : indexes.sort((a, b) => a - b).map((index) => source.messages[index]!);
    const height = view.estimatedHeight;
    const before = view.transcript().entries.at(-1);
    const status = view.card()?.status;
    view.publish(
      rows,
      changes,
      cards,
      scope.parent?.marker?.type === "subagent-started"
        ? (scope.parent.marker.summary ?? null)
        : null,
      scope.marker,
    );
    resizeAncestors(scope, view.estimatedHeight - height);
    outputChanged ||= before !== view.transcript().entries.at(-1) || status !== view.card()?.status;
    if (view.request === null) pendingChanged = pending.delete(scope.key) || pendingChanged;
    else {
      const index = scope.indexes.findLast(
        (index) =>
          source.messages[index]?.requestId === view.request!.requestId &&
          source.messages[index]?.type.endsWith("-requested"),
      )!;
      const before = pending.get(scope.key);
      pendingChanged ||= before?.index !== index || before.request.key !== view.request.key;
      pending.set(scope.key, { request: view.request, index });
    }
    if (view.active || view.card()?.asideActive === true) active.add(scope.key);
    else active.delete(scope.key);
    if (scope.key === "") {
      setHistory(submittedPrompts(rows));
      setLatestPlan(latestCompletedPlan(rows));
      setPendingLegacyImageCount(countPendingLegacyImages(rows));
      setTurnStartedAt(view.startedAt);
    }
  };

  const resizeAncestors = (scope: ConversationRows, delta: number): void => {
    if (scope.container === null) return;
    let child = scope;
    while (child.container !== null && child.container.key !== "") {
      const parent = viewFor(child.container);
      const before = parent.estimatedHeight;
      parent.childResized(viewFor(child).linked.id, delta);
      delta = parent.estimatedHeight - before;
      child = child.container;
    }
    const id = viewFor(child).linked.id;
    resized.set(id, (resized.get(id) ?? 0) + delta);
  };

  const retainRequests = (scope: ConversationRows): void => {
    for (let child = scope; child.container !== null; child = child.container) {
      const parent = viewFor(child.container);
      const before = parent.estimatedHeight;
      parent.setCardPending(
        viewFor(child).linked.id,
        viewFor(child).transcript().pendingRowIndexes.length > 0,
      );
      resizeAncestors(child.container, parent.estimatedHeight - before);
    }
  };

  const apply = (
    rows: AgentPaneConversationRows,
    changes: AgentPaneUpdate[],
    live: boolean,
  ): void =>
    batch(() => {
      outputChanged = false;
      pendingChanged = changes.length === 0;
      resized = new Map();
      source = rows;
      if (changes.length === 0) {
        pending.clear();
        active.clear();
        for (const cell of byId.values()) cell[1](undefined);
        for (const [key, view] of views) {
          if (source.conversations.has(key)) continue;
          view.dispose();
          views.delete(key);
        }
        const reconstruct = (scope: ConversationRows): void => {
          for (const child of scope.cards) reconstruct(child);
          project(scope, []);
        };
        reconstruct(source.get(""));
        for (const scope of source.conversations.values()) retainRequests(scope);
      } else {
        const changed = new Map<ConversationRows, AgentPaneUpdate[]>();
        for (const message of changes) {
          const scope = source.get(conversationKey(message));
          const own = changed.get(scope) ?? [];
          own.push(message);
          changed.set(scope, own);
        }
        const affected = new Set([...changed.keys(), ...source.placements]);
        const depth = (scope: ConversationRows): number => {
          let count = 0;
          for (let parent = scope.container; parent !== null; parent = parent.container) count++;
          return count;
        };
        for (const scope of [...affected].sort((left, right) => depth(right) - depth(left)))
          project(scope, source.placements.has(scope) ? [] : (changed.get(scope) ?? []));
        for (const scope of affected) retainRequests(scope);
      }
      source.placements.clear();
      setCardChanges([...resized].map(([id, delta]) => ({ id, delta })));
      if (pendingChanged) {
        let latest: { request: PendingRequest; index: number } | undefined;
        for (const candidate of pending.values())
          if (latest === undefined || candidate.index > latest.index) latest = candidate;
        setRequest(latest?.request ?? null);
      }
      setInterruptible(active.size > 0);
      if (outputChanged) setLatestOutput((count) => count + 1);
      const { entries, latestResultIndex } = main.transcript();
      const replyId = latestResultIndex === null ? null : entries[latestResultIndex]!.id;
      if (replyId !== latestReplyId) {
        latestReplyId = replyId;
        setLiveReplyId(live ? replyId : null);
      }
    });

  createEffect(
    on(
      createMemo(() => [thoughtDisplay(), progressDisplay()]),
      () => apply(source, [], false),
      { defer: true },
    ),
  );

  const model: MutableAgentPaneModel = {
    cardChanges,
    generation,
    history,
    historyComplete,
    interruptible,
    latestOutput,
    latestPlan,
    live,
    liveReplyId,
    pendingLegacyImageCount,
    session,
    turnStartedAt,
    transcript: main.transcript,
    turnActive: () => main.transcript().turnActive,
    pendingRequestKind: () => request()?.kind ?? null,
    keyboardRequestKey: () => request()?.key ?? null,
    keyboardApprovalId: () => (request()?.kind === "approval" ? request()!.requestId : null),
    publish: (rows, changes) => apply(rows, changes, true),
    load: (rows) => apply(rows, [], false),
    conversation(id) {
      return membership(id)[0]()?.linked;
    },
    setHistoryComplete(complete) {
      batch(() => {
        setHistoryComplete(complete);
        if (!complete) setLive(false);
      });
    },
    noteLive: () => setLive(true),
    reset() {
      expanded.clear();
      apply(new AgentPaneConversationRows([]), [], false);
      clearAgentInputDrafts(session);
      setGeneration((value) => value + 1);
    },
    activityStep(itemId) {
      for (const view of views.values()) {
        const step = view.activityStep(itemId);
        if (step !== undefined) return step;
      }
      return undefined;
    },
    setActivityExpanded(id, expanded) {
      for (const [key, view] of views) {
        const before = view.estimatedHeight;
        if (!view.setExpanded(id, expanded)) continue;
        resized = new Map();
        resizeAncestors(source.get(key), view.estimatedHeight - before);
        setCardChanges([...resized].map(([id, delta]) => ({ id, delta })));
        break;
      }
    },
  };
  return model;
}

function countPendingLegacyImages(messages: readonly AgentPaneUpdate[]): number {
  let count = 0;
  for (const message of messages) {
    if (message.type === "user-image") {
      if (message.status === "attached") {
        count += 1;
      } else if (message.status === "submitted") {
        count = Math.max(0, count - 1);
      }
    }
  }
  return count;
}
