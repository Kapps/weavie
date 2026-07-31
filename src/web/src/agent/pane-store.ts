import { createSignal } from "solid-js";
import { type AgentPaneUpdate, type ClientSession, registerSessionFeature } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";

const EMPTY: AgentPaneUpdate[] = [];
const [messages, setMessages] = createSignal(new Map<ClientSession, AgentPaneUpdate[]>());

function publish(session: ClientSession, updates: AgentPaneUpdate[]): void {
  setMessages((previous) => new Map(previous).set(session, updates));
}

registerSessionFeature((session) => {
  const accumulator = new AgentPaneAccumulator((callback) => requestAnimationFrame(callback));
  const feature = session.feature("agent");
  const offPane = feature.on<AgentPaneUpdate>("pane", (message) =>
    accumulator.ingest("pane", message, (updates) => publish(session, updates)),
  );
  const offBatch = feature.on<{ messages: AgentPaneUpdate[] }>("paneBatch", ({ messages }) => {
    for (const message of messages) {
      accumulator.ingest("pane", message, (updates) => publish(session, updates));
    }
  });
  const offReset = feature.on("paneReset", () =>
    accumulator.reset("pane", (updates) => publish(session, updates)),
  );
  return () => {
    offPane();
    offBatch();
    offReset();
    setMessages((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});

export function agentPaneMessages(session: ClientSession | null): AgentPaneUpdate[] {
  return session === null ? EMPTY : (messages().get(session) ?? EMPTY);
}
