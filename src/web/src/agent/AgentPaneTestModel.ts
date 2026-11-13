import { vi } from "vitest";

vi.mock("solid-js", async () => await vi.importActual("solid-js/dist/solid.js"));
vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

import { createRoot } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import { AgentPaneConversationRows } from "./AgentPaneConversationRows";
import { createAgentPaneModel } from "./AgentPaneModel";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

/** Test callers providing folded snapshots use the same membership index as the live accumulator. */
export function createTestAgentPaneModel(session: ClientSession) {
  const model = createAgentPaneModel(
    session,
    () => "all",
    () => "all",
  );
  const publish = (messages: AgentPaneUpdate[], changes: AgentPaneUpdate[]): void => {
    const rows = new AgentPaneConversationRows(messages);
    messages.forEach((_, index) => {
      rows.register(index);
    });
    if (changes.length > 0) rows.placements.clear();
    model.publish(rows, changes);
  };
  return { ...model, publish, replace: (rows: AgentPaneUpdate[]) => publish(rows, []) };
}

/** Raw journal fixtures enter the real fold once, just as history reconstruction does. */
export function toAgentTranscript(messages: readonly AgentPaneUpdate[]): AgentTranscriptEntry[] {
  return createRoot((dispose) => {
    const model = createAgentPaneModel(
      {} as ClientSession,
      () => "all",
      () => "all",
    );
    let flush = () => {};
    const accumulator = new AgentPaneAccumulator((callback) => {
      flush = callback;
    });
    accumulator.ingestBatch("test", messages, model.publish);
    flush();
    const materialize = (entries: readonly AgentTranscriptEntry[]): AgentTranscriptEntry[] =>
      entries.map((entry) => {
        const { activityDetails, ...snapshot } = entry;
        const reader = activityDetails?.retain();
        const details = reader?.steps() ?? entry.details;
        reader?.release();
        return {
          ...snapshot,
          details,
          ...(entry.asideEntries === undefined
            ? {}
            : { asideEntries: materialize(entry.asideEntries) }),
        };
      });
    const entries = materialize(model.transcript().entries);
    dispose();
    return entries;
  });
}
