import { createSignal } from "solid-js";
import { type AgentPaneUpdate, type ClientSession, registerSessionFeature } from "../bridge";
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
  const model = createAgentPaneModel(session);
  setModels((previous) => new Map(previous).set(session, model));
  const accumulator = new AgentPaneAccumulator((callback) => requestAnimationFrame(callback));
  const feature = session.feature("agent");
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
  const offPane = feature.on<AgentPaneUpdate>("pane", ingest);
  const offBatch = feature.on<{ messages: AgentPaneUpdate[] }>("paneBatch", ({ messages }) => {
    for (const message of messages) {
      applyMessageState(message);
    }
    accumulator.ingestBatch("pane", messages, publish);
  });
  const offSnapshot = feature.on<{ messages: AgentPaneUpdate[] }>(
    "paneSnapshot",
    ({ messages }) => {
      clearAgentInputDrafts(session);
      accumulator.replace("pane", messages, (updates) => {
        applyNewDrafts(updates);
        model.replace(updates);
      });
    },
  );
  const offReset = feature.on("paneReset", () => {
    appliedDrafts = 0;
    accumulator.reset("pane", () => model.reset());
  });
  return () => {
    offPane();
    offBatch();
    offSnapshot();
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
