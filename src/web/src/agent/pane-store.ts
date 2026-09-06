import {
  type AgentPaneHistoryFragment,
  type AgentPaneUpdate,
  type AgentPaneWireUpdate,
  type ClientSession,
  registerSessionFeature,
} from "../bridge";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import {
  agentInputRequestKey,
  clearAgentInputDraft,
  clearAgentInputDrafts,
} from "./AgentInputDrafts";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import { isAgentPaneWireUpdate } from "./AgentPaneHistoryAccumulator";
import { type AgentPaneModel, createAgentPaneModel } from "./AgentPaneModel";
import { clearAsideReplyState, clearAsideReplyStates } from "./aside-reply-store";
import { setComposerDraft } from "./composer-store";

export type { AgentPaneModel, AgentSectionLabel } from "./AgentPaneModel";

const models = createSessionOwnedState(createAgentPaneModel);
const authenticationTerminals = createSessionOwnedState(() => false);
const bodyLoaders = new WeakMap<ClientSession, (message: AgentPaneWireUpdate) => Promise<void>>();

registerSessionFeature((session) => {
  const accumulator = new AgentPaneAccumulator(
    (callback) => requestAnimationFrame(callback),
    // Deferred: the accumulator raises this while it is still writing the record that changed the generation,
    // and resyncing mutates that same slot. `resyncPane` is hoisted; it only runs once setup has finished.
    () => queueMicrotask(resyncPane),
  );
  const feature = session.feature("agent");
  let historyAbort: AbortController | null = null;
  let historyComplete = false;
  let historyGeneration: number | null = null;
  let historyReadId: string | null = null;
  let historyRevision: number | null = null;
  let bodyAbort = new AbortController();
  const bodyRequests = new Map<string, Promise<void>>();
  const resetBodyRequests = (): void => {
    bodyAbort.abort();
    bodyAbort = new AbortController();
    bodyRequests.clear();
  };
  bodyLoaders.set(session, (message) => {
    const key = `${message.generation}:${message.ordinal}:${message.revision}`;
    const existing = bodyRequests.get(key);
    if (existing !== undefined) return existing;
    const request = feature
      .request<AgentPaneWireUpdate, { generation: number; ordinal: number }>(
        "historyBody",
        {
          generation: message.generation,
          ordinal: message.ordinal,
        },
        bodyAbort.signal,
      )
      .then((body) => {
        if (
          !isAgentPaneWireUpdate(body) ||
          body.bodyDeferred === true ||
          body.generation !== message.generation ||
          body.ordinal !== message.ordinal
        ) {
          throw new Error("Received an invalid agent message body.");
        }
        accumulator.hydrate("pane", body, publish);
      })
      .finally(() => {
        if (bodyRequests.get(key) === request) bodyRequests.delete(key);
      });
    bodyRequests.set(key, request);
    return request;
  });

  interface HistoryCursor {
    readId: string;
    before: number;
    jsonBefore: number | null;
  }

  interface HistoryPage {
    generation: number;
    messages: AgentPaneHistoryFragment[];
    readId: string;
    revision: number;
    cursor: HistoryCursor | null;
  }

  const startHistory = (): void => {
    if (historyAbort !== null || historyComplete) {
      return;
    }
    const abort = new AbortController();
    historyAbort = abort;
    clearAgentInputDrafts(session);
    void loadHistory(abort)
      .catch((error: unknown) => {
        if (!abort.signal.aborted) {
          session.connection.reportError(error);
        }
      })
      .finally(() => {
        if (historyAbort === abort) {
          historyAbort = null;
        }
      });
  };

  const model = models.get(session)!;

  const offHello = session.connection.onHello(() => {
    resetBodyRequests();
    historyComplete = false;
    historyAbort?.abort();
    historyAbort = null;
    historyReadId = null;
    accumulator.abandonHistory("pane");
    startHistory();
  });

  async function loadHistory(abort: AbortController): Promise<void> {
    let cursor: HistoryCursor | null = null;
    do {
      const page: HistoryPage = await feature.request<
        HistoryPage,
        {
          cursor: HistoryCursor | null;
          knownGeneration: number | null;
          knownRevision: number | null;
        }
      >(
        "historyPage",
        {
          cursor,
          knownGeneration: cursor === null ? historyGeneration : null,
          knownRevision: cursor === null ? historyRevision : null,
        },
        abort.signal,
      );
      if (abort.signal.aborted) {
        feature.publish("historyClose", { readId: page.readId });
        return;
      }
      historyReadId = page.cursor?.readId ?? null;
      accumulator.mergeHistory(
        "pane",
        page.generation,
        page.messages,
        page.cursor === null,
        publish,
      );
      cursor = page.cursor;
      if (cursor !== null) {
        await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      } else {
        historyGeneration = page.generation;
        historyRevision = page.revision;
      }
    } while (cursor !== null);
    historyComplete = true;
  }

  let appliedDrafts = 0;
  const applyMessageState = (message: AgentPaneUpdate): void => {
    if (message.type === "input-resolved") {
      clearAgentInputDraft(session, agentInputRequestKey(message));
    }
    if (message.type === "side-conversation-failed" && message.conversationId) {
      clearAsideReplyState(session, message.conversationId);
    }
  };
  const applyNewDrafts = (messages: readonly AgentPaneUpdate[]): void => {
    let occurrence = 0;
    for (const message of messages) {
      if (message.type !== "draft") {
        continue;
      }
      occurrence += 1;
      if (occurrence > appliedDrafts) {
        setComposerDraft(session, message.text ?? "");
      }
    }
    appliedDrafts = Math.max(appliedDrafts, occurrence);
  };
  const publish = (updates: AgentPaneUpdate[], changes: AgentPaneUpdate[]): void => {
    applyNewDrafts(updates);
    model.publish(updates, changes);
  };
  const ingest = (message: AgentPaneUpdate): void => {
    applyMessageState(message);
    accumulator.ingest("pane", message, publish);
  };
  const offPane = feature.on<AgentPaneWireUpdate>("pane", ingest);
  const offAuthenticationTerminal = feature.on<{ active: boolean }>(
    "authenticationTerminal",
    ({ active }) => {
      authenticationTerminals.update(session, () => active);
    },
  );
  const offBatch = feature.on<{ messages: AgentPaneWireUpdate[] }>("paneBatch", ({ messages }) => {
    for (const message of messages) {
      applyMessageState(message);
    }
    accumulator.ingestBatch("pane", messages, publish);
  });
  // Re-fetch the transcript from the host, discarding every ordinal this client holds. Reached two ways: the
  // host announcing a reset, and a live record arriving from a newer generation, which says the same thing.
  function resyncPane(): void {
    resetBodyRequests();
    historyAbort?.abort();
    historyAbort = null;
    if (historyReadId !== null) {
      feature.publish("historyClose", { readId: historyReadId });
      historyReadId = null;
    }
    historyComplete = false;
    historyGeneration = null;
    historyRevision = null;
    appliedDrafts = 0;
    clearAsideReplyStates(session);
    accumulator.reset("pane", () => model.reset());
    startHistory();
  }

  const offReset = feature.on("paneReset", resyncPane);
  startHistory();
  return () => {
    bodyAbort.abort();
    bodyLoaders.delete(session);
    historyAbort?.abort();
    if (historyReadId !== null) {
      feature.publish("historyClose", { readId: historyReadId });
      historyReadId = null;
    }
    offPane();
    offAuthenticationTerminal();
    offBatch();
    offReset();
    offHello();
  };
});

export function loadAgentBody(session: ClientSession, message: AgentPaneUpdate): Promise<void> {
  const load = bodyLoaders.get(session);
  if (load === undefined || !isAgentPaneWireUpdate(message)) {
    return Promise.reject(new Error("This agent message is no longer available."));
  }
  return load(message);
}

export function agentPaneModel(session: ClientSession | null): AgentPaneModel | null {
  return models.get(session) ?? null;
}

export function agentAuthenticationTerminalActive(session: ClientSession | null): boolean {
  return authenticationTerminals.get(session) === true;
}
