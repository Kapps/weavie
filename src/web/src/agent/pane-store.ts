import { createSignal } from "solid-js";
import { type AgentPaneUpdate, type ClientSession, registerSessionFeature } from "../bridge";
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
  const model = createAgentPaneModel(session);
  setModels((previous) => new Map(previous).set(session, model));
  const accumulator = new AgentPaneAccumulator((callback) => requestAnimationFrame(callback));
  const feature = session.feature("agent");
  const ingest = (message: AgentPaneUpdate): void => {
    if (message.type === "input-resolved") {
      clearAgentInputDraft(session, agentInputRequestKey(message));
    }
    accumulator.ingest("pane", message, (updates) => model.publish(updates));
  };
  const offPane = feature.on<AgentPaneUpdate>("pane", ingest);
  const offBatch = feature.on<{ messages: AgentPaneUpdate[] }>("paneBatch", ({ messages }) => {
    for (const message of messages) {
      ingest(message);
    }
  });
  const offReset = feature.on("paneReset", () => accumulator.reset("pane", () => model.reset()));
  return () => {
    offPane();
    offBatch();
    offReset();
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
