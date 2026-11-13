import { createSignal } from "solid-js";
import {
  type AgentSlashEntry,
  type ClientSession,
  hostConnection,
  registerHostFeature,
} from "../bridge";
import type { CommandUsage, CommandUsageSnapshot } from "./ranking";

const [snapshots, setSnapshots] = createSignal(
  new Map<string, CommandUsageSnapshot & { receivedAt: number }>(),
);
const apply = (backend: string, snapshot: CommandUsageSnapshot): void => {
  setSnapshots((previous) =>
    new Map(previous).set(backend, { ...snapshot, receivedAt: performance.now() }),
  );
};

registerHostFeature((connection) => {
  const feature = connection.host.feature("commandUsage");
  const offChanged = feature.on<CommandUsageSnapshot>("changed", (snapshot) =>
    apply(connection.id, snapshot),
  );
  const offHello = connection.onHello((hello) => apply(connection.id, hello.commandUsage));
  return () => {
    offChanged();
    offHello();
    setSnapshots((previous) => {
      const next = new Map(previous);
      next.delete(connection.id);
      return next;
    });
  };
});

export function commandUsage(backend: string): readonly CommandUsage[] {
  const snapshot = snapshots().get(backend);
  if (!snapshot?.enabled) return [];
  const decay = 0.5 ** ((performance.now() - snapshot.receivedAt) / snapshot.halfLifeMilliseconds);
  return snapshot.entries.map((entry) => ({ ...entry, weight: entry.weight * decay }));
}

/** Capture the preference with the user's choice, before an asynchronous command can change it. */
export function captureCommandUsage(backend: string, id: string, query: string): () => void {
  const enabled = snapshots().get(backend)?.enabled === true;
  return () => {
    if (enabled)
      hostConnection(backend)?.host.feature("commandUsage").publish("record", { id, query });
  };
}

export function slashUsageId(
  session: ClientSession,
  entry: Pick<AgentSlashEntry, "kind" | "name" | "commandId">,
): string {
  if (entry.commandId !== null) return entry.commandId;
  if (entry.kind === "mcpPrompt") return `prompt:${entry.name}`;
  const provider = session.connection.currentCatalog.find(
    (item) => item.id === session.address.slot,
  )?.providerId;
  if (provider === undefined) throw new Error("The slash command has no owning provider");
  return JSON.stringify([provider, entry.name]);
}
