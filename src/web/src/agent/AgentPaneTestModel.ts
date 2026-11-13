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
    const expand = (entries: readonly AgentTranscriptEntry[]): void => {
      for (const entry of entries) {
        if (entry.kind === "activity") model.setActivityExpanded(entry.id, true);
        if (entry.asideEntries !== undefined) expand(entry.asideEntries);
      }
    };
    expand(model.transcript().entries);
    const entries = [...model.transcript().entries];
    dispose();
    return entries;
  });
}
