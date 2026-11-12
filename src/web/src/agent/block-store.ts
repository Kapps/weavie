import { type Accessor, createRoot, createSignal, type Setter } from "solid-js";
import type { AgentBlockRecord, ClientSession } from "../bridge";
import { createSessionOwnedResource } from "../messaging/session-owned-state";
import { blockKey } from "./AgentPaneIdentity";
import type { ColumnFilter, TableSort } from "./agent-table-model";

/** How the user has arranged a table: its sort, and each column's filter by column name. */
export interface TableView {
  sort: Accessor<TableSort>;
  setSort: Setter<TableSort>;
  filters: Accessor<ReadonlyMap<string, ColumnFilter>>;
  setFilters: Setter<ReadonlyMap<string, ColumnFilter>>;
}

interface BlockSignals {
  blocks: Map<
    string,
    [Accessor<AgentBlockRecord | undefined>, Setter<AgentBlockRecord | undefined>]
  >;
  views: Map<string, TableView>;
  // The transcript generation the held blocks belong to; a newer one voids them all.
  generation: number;
  dispose: () => void;
}

// One signal per block, so a refreshing chart re-renders only itself.
const stores = createSessionOwnedResource<BlockSignals>(
  () => createRoot((dispose) => ({ blocks: new Map(), views: new Map(), generation: -1, dispose })),
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

/** Applies block records: a newer generation replaces every block, and an older record changes nothing. */
export function putBlocks(session: ClientSession, records: readonly AgentBlockRecord[]): void {
  const store = stores.get(session);
  if (store === undefined) return;
  for (const record of records) {
    if (record.generation < store.generation) continue;
    if (record.generation > store.generation) {
      clear(store);
      store.generation = record.generation;
    }
    const [read, write] = signal(
      store,
      blockKey(record.providerId, record.conversationId, record.block.id),
    );
    if ((read()?.revision ?? -1) < record.revision) write(record);
  }
}

/** Forgets every block when the transcript is replaced. */
export function clearBlocks(session: ClientSession): void {
  const store = stores.get(session);
  if (store !== undefined) clear(store);
}

function clear(store: BlockSignals): void {
  for (const [, write] of store.blocks.values()) write(undefined);
  store.views.clear();
}

/** The table view of the block `key` names, held with the block so a row that leaves the transcript keeps it. */
export function tableView(session: ClientSession, key: string): TableView | undefined {
  const store = stores.get(session);
  if (store === undefined) return undefined;
  let view = store.views.get(key);
  if (view === undefined) {
    view = createRoot(() => {
      const [sort, setSort] = createSignal<TableSort>(null);
      const [filters, setFilters] = createSignal<ReadonlyMap<string, ColumnFilter>>(new Map());
      return { sort, setSort, filters, setFilters };
    });
    store.views.set(key, view);
  }
  return view;
}

/** The latest state of the block `key` names, tracked reactively. */
export function agentBlock(session: ClientSession, key: string): AgentBlockRecord | undefined {
  const store = stores.get(session);
  return store === undefined ? undefined : signal(store, key)[0]();
}
