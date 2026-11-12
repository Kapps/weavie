import { type Accessor, createRoot, createSignal, type Setter } from "solid-js";
import type { AgentBlockRecord, ClientSession } from "../bridge";
import { createSessionOwnedResource } from "../messaging/session-owned-state";

/** The key of block `blockId` in one provider conversation. */
export function blockKey(
  providerId: string,
  conversationId: string | null | undefined,
  blockId: string,
): string {
  return `${providerId}\u0000${conversationId ?? ""}\u0000${blockId}`;
}

interface BlockSignals {
  blocks: Map<
    string,
    [Accessor<AgentBlockRecord | undefined>, Setter<AgentBlockRecord | undefined>]
  >;
  dispose: () => void;
}

// One signal per block, so a refreshing chart re-renders only itself.
const stores = createSessionOwnedResource<BlockSignals>(
  () => createRoot((dispose) => ({ blocks: new Map(), dispose })),
  (_session, store) => store.dispose(),
);

function signal(store: BlockSignals, key: string) {
  let entry = store.blocks.get(key);
  if (entry === undefined) {
    entry = createRoot(() =>
      createSignal<AgentBlockRecord | undefined>(undefined, { equals: false }),
    );
    store.blocks.set(key, entry);
  }
  return entry;
}

/** Applies block records; a record older than the one already held is ignored. */
export function putBlocks(session: ClientSession, records: readonly AgentBlockRecord[]): void {
  const store = stores.get(session);
  if (store === undefined) return;
  for (const record of records) {
    const [read, write] = signal(
      store,
      blockKey(record.providerId, record.conversationId, record.block.id),
    );
    if ((read()?.revision ?? -1) < record.revision) write(record);
  }
}

/** Forgets every block when the transcript is replaced. */
export function clearBlocks(session: ClientSession): void {
  for (const [, write] of stores.get(session)?.blocks.values() ?? []) write(undefined);
}

/** The latest state of the block `key` names, tracked reactively. */
export function agentBlock(session: ClientSession, key: string): AgentBlockRecord | undefined {
  const store = stores.get(session);
  return store === undefined ? undefined : signal(store, key)[0]();
}
