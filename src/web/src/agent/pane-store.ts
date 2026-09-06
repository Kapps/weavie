import {
  type AgentPaneUpdate,
  type AgentPaneWireUpdate,
  type ClientSession,
  sessionResourceUrl,
} from "../bridge";
import { keyHintInCatalog } from "../commands/key-hint";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { readJsonStream } from "../messaging/json-stream";
import {
  createSessionOwnedResource,
  createSessionOwnedState,
} from "../messaging/session-owned-state";
import { clearNotification, notify } from "../notify/notify";
import {
  agentInputRequestKey,
  clearAgentInputDraft,
  clearAgentInputDrafts,
} from "./AgentInputDrafts";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import { type AgentPaneModel, createAgentPaneModel } from "./AgentPaneModel";
import { clearAsideReplyState, clearAsideReplyStates } from "./aside-reply-store";
import { setComposerDraft } from "./composer-store";

export type { AgentPaneModel, AgentSectionLabel } from "./AgentPaneModel";

const models = createSessionOwnedState(createAgentPaneModel);
const authenticationTerminals = createSessionOwnedState(() => false);

const histories = createSessionOwnedResource(createHistory, (_session, history) =>
  history.dispose(),
);

function createHistory(session: ClientSession) {
  const errorKey = `agent-history:${session.connection.id}:${session.address.incarnation}`;
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
  let historyRevision: number | null = null;

  interface HistorySnapshot {
    generation: number;
    revision: number;
    count: number;
    complete: boolean;
    messages: AgentPaneWireUpdate[];
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
          notify(
            "error",
            `History for ${session.address.slot} is incomplete: ${String(error)}. Use Reload Agent History${keyHintInCatalog(session.connection.id, CommandIds.reloadAgentHistory)} to retry.`,
            errorKey,
          );
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
    historyComplete = false;
    historyAbort?.abort();
    historyAbort = null;
    accumulator.abandonHistory("pane");
    startHistory();
  });

  async function loadHistory(abort: AbortController): Promise<void> {
    const url = sessionResourceUrl(session, "/weavie-agent-history");
    if (historyGeneration !== null && historyRevision !== null) {
      url.searchParams.set("knownGeneration", historyGeneration.toString());
      url.searchParams.set("knownRevision", historyRevision.toString());
    }
    const response = await fetch(url, { signal: abort.signal });
    let received = 0;
    let baseline: HistorySnapshot | null = null;
    for await (const batch of readJsonStream<HistorySnapshot>(response)) {
      abort.signal.throwIfAborted();
      const firstBatch = baseline === null;
      if (
        baseline !== null &&
        (batch.generation !== baseline.generation ||
          batch.revision !== baseline.revision ||
          batch.count !== baseline.count)
      ) {
        throw new Error("History response changed its snapshot.");
      }
      baseline = batch;
      received += batch.messages.length;
      if (received > batch.count || (batch.complete && received !== batch.count)) {
        throw new Error("History response has an incorrect record count.");
      }
      accumulator.mergeHistory("pane", batch.generation, batch.messages, batch.complete, publish);
      if (batch.complete) {
        historyGeneration = batch.generation;
        historyRevision = batch.revision;
        historyComplete = true;
        clearNotification(errorKey);
        return;
      }
      if (firstBatch) {
        await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      }
    }
    throw new Error("History response ended before completion.");
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
    historyAbort?.abort();
    historyAbort = null;
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
  return {
    reload: () => {
      historyAbort?.abort();
      historyAbort = null;
      historyComplete = false;
      accumulator.abandonHistory("pane");
      startHistory();
    },
    dispose: () => {
      historyAbort?.abort();
      clearNotification(errorKey);
      offPane();
      offAuthenticationTerminal();
      offBatch();
      offReset();
      offHello();
    },
  };
}

registerCommand(CommandIds.reloadAgentHistory, (_args, { session }) => {
  if (session === null) return false;
  histories.get(session)?.reload();
});

export function agentPaneModel(session: ClientSession | null): AgentPaneModel | null {
  return models.get(session) ?? null;
}

export function agentAuthenticationTerminalActive(session: ClientSession | null): boolean {
  return authenticationTerminals.get(session) === true;
}
