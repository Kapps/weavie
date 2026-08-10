import { createSignal } from "solid-js";
import {
  type AgentPaneHistoryFragment,
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

export type { AgentPaneModel, AgentSectionLabel } from "./AgentPaneModel";

const [models, setModels] = createSignal(new Map<ClientSession, AgentPaneModel>());

registerSessionFeature((session) => {
  const accumulator = new AgentPaneAccumulator((callback) => requestAnimationFrame(callback));
  const feature = session.feature("agent");
  let historyAbort: AbortController | null = null;
  let historyComplete = false;
  let historyWanted = false;

  interface HistoryCursor {
    generation: number;
    ceiling: number;
    before: number;
    jsonBefore: number | null;
    jsonRevision: number | null;
  }

  interface HistoryPage {
    generation: number;
    restarted: boolean;
    messages: AgentPaneHistoryFragment[];
    cursor: HistoryCursor | null;
  }

  const startHistory = (): void => {
    historyWanted = true;
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

  const model = createAgentPaneModel(session, startHistory);
  setModels((previous) => new Map(previous).set(session, model));

  const offHello = session.connection.onHello(() => {
    if (historyWanted) {
      historyComplete = false;
      historyAbort?.abort();
      historyAbort = null;
      startHistory();
    }
  });

  const loadHistory = async (abort: AbortController): Promise<void> => {
    let cursor: HistoryCursor | null = null;
    do {
      const page: HistoryPage = await feature.request<
        HistoryPage,
        { cursor: HistoryCursor | null }
      >("historyPage", { cursor }, abort.signal);
      if (abort.signal.aborted) {
        return;
      }
      if (page.restarted) {
        accumulator.restartHistory("pane", page.generation);
      }
      accumulator.mergeHistory("pane", page.generation, page.messages, (updates) =>
        model.publish(updates),
      );
      cursor = page.cursor;
      if (cursor !== null) {
        await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      }
    } while (cursor !== null);
    historyComplete = true;
  };

  const ingest = (message: AgentPaneWireUpdate): void => {
    if (message.type === "input-resolved") {
      clearAgentInputDraft(session, agentInputRequestKey(message));
    }
    accumulator.ingest("pane", message, (updates) => model.publish(updates));
  };
  const offPane = feature.on<AgentPaneWireUpdate>("pane", ingest);
  const offBatch = feature.on<{ messages: AgentPaneWireUpdate[] }>("paneBatch", ({ messages }) => {
    for (const message of messages) {
      ingest(message);
    }
  });
  const offReset = feature.on("paneReset", () => {
    historyAbort?.abort();
    historyAbort = null;
    historyComplete = false;
    accumulator.reset("pane", () => model.reset());
    if (historyWanted) {
      startHistory();
    }
  });
  return () => {
    historyAbort?.abort();
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
