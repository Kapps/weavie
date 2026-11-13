import { createSignal } from "solid-js";
import {
  type AgentSlashEntry,
  type ClientSession,
  hostConnection,
  registerHostFeature,
} from "../bridge";
import { notify } from "../notify/notify";
import type { CommandUsage } from "./ranking";

interface UsageSnapshot {
  enabled: boolean;
  halfLifeMilliseconds: number;
  entries: CommandUsage[];
}
const [snapshots, setSnapshots] = createSignal(
  new Map<string, UsageSnapshot & { receivedAt: number }>(),
);
const apply = (backend: string, snapshot: UsageSnapshot) =>
  setSnapshots((previous) =>
    new Map(previous).set(backend, { ...snapshot, receivedAt: performance.now() }),
  );

registerHostFeature((connection) => {
  const feature = connection.host.feature("commandUsage");
  let revision = 0;
  const offChanged = feature.on<UsageSnapshot>("changed", (snapshot) => {
    revision++;
    apply(connection.id, snapshot);
  });
  const offHello = connection.onHello(() => {
    const requested = ++revision;
    void feature
      .request<UsageSnapshot>("get", {})
      .then((snapshot) => {
        if (revision === requested) apply(connection.id, snapshot);
      })
      .catch((error) => notify("warn", `Could not load command learning: ${String(error)}`));
  });
  return () => {
    revision++;
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

export function recordCommandUsage(backend: string, id: string, query: string): void {
  hostConnection(backend)?.host.feature("commandUsage").publish("record", { id, query });
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
