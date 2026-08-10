import { createSignal } from "solid-js";
import {
  type AgentPaneHistoryFragment,
  type AgentPaneUpdate,
  type AgentPaneWireUpdate,
  type ClientSession,
  registerSessionFeature,
} from "../bridge";
import {
  agentInputRequestKey,
  clearAgentInputDraft,
  clearAgentInputDrafts,
} from "./AgentInputDrafts";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import { type AgentPaneModel, createAgentPaneModel } from "./AgentPaneModel";
import { setComposerDraft } from "./composer-store";

export type { AgentPaneModel, AgentSectionLabel } from "./AgentPaneModel";

const [models, setModels] = createSignal(new Map<ClientSession, AgentPaneModel>());

registerSessionFeature((session) => {
  const accumulator = new AgentPaneAccumulator((callback) => requestAnimationFrame(callback));
  const feature = session.feature("agent");
  let historyAbort: AbortController | null = null;
  let historyActive = false;
  let historyComplete = false;
  let historyGeneration: number | null = null;
  let historyReadId: string | null = null;
  let historyRevision: number | null = null;

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
    if (!historyActive || historyAbort !== null || historyComplete) {
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

  const activateHistory = (): (() => void) => {
    historyActive = true;
    startHistory();
    return () => {
      historyActive = false;
      historyAbort?.abort();
      historyAbort = null;
      accumulator.abandonHistory("pane");
      if (historyReadId !== null) {
        feature.publish("historyClose", { readId: historyReadId });
        historyReadId = null;
      }
    };
  };

  const model = createAgentPaneModel(session, activateHistory);
  setModels((previous) => new Map(previous).set(session, model));

  const offHello = session.connection.onHello(() => {
    historyComplete = false;
    historyAbort?.abort();
    historyAbort = null;
    historyReadId = null;
    accumulator.abandonHistory("pane");
    startHistory();
  });

  const loadHistory = async (abort: AbortController): Promise<void> => {
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
  };

  let appliedDrafts = 0;
  const applyMessageState = (message: AgentPaneUpdate): void => {
    if (message.type === "input-resolved") {
      clearAgentInputDraft(session, agentInputRequestKey(message));
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
  const offBatch = feature.on<{ messages: AgentPaneWireUpdate[] }>("paneBatch", ({ messages }) => {
    for (const message of messages) {
      applyMessageState(message);
    }
    accumulator.ingestBatch("pane", messages, publish);
  });
  const offReset = feature.on("paneReset", () => {
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
    accumulator.reset("pane", () => model.reset());
    startHistory();
  });
  return () => {
    historyAbort?.abort();
    if (historyReadId !== null) {
      feature.publish("historyClose", { readId: historyReadId });
      historyReadId = null;
    }
    offPane();
    offBatch();
    offReset();
    offHello();
    clearAgentInputDrafts(session);
    setModels((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});

export function agentPaneModel(session: ClientSession | null): AgentPaneModel | null {
  return session === null ? null : (models().get(session) ?? null);
}
