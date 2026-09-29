import { createEffect, createRoot } from "solid-js";
import {
  type ClientSession,
  registerHostFeature,
  selectClientSession,
  selectedSession,
  sessionForSlot,
} from "../bridge";
import { chooseOpenSlot } from "./open-target";
import { openFilesIn } from "./reveal";

// A path the OS handed a host — an "Open With", or `weavie <path>`. The host forwards it rather than opening
// it itself, because the session the user is looking at may belong to a different backend than the one the
// desktop launched.
interface PendingOpen {
  path: string;
  backendId: string;
  fallbackSlot: string | null;
}

const pending: PendingOpen[] = [];

function target(open: PendingOpen): ClientSession | undefined {
  const current = selectedSession();
  const slot = chooseOpenSlot(
    current === null ? null : { backendId: current.connection.id, slot: current.address.slot },
    open,
  );
  return slot === null ? undefined : sessionForSlot(open.backendId, slot);
}

function flush(): void {
  // A cold launch is handed its path before anything is selected, so hold rather than open into nothing.
  if (selectedSession() === null) {
    return;
  }
  const batches = new Map<ClientSession, string[]>();
  for (const open of pending.splice(0)) {
    const session = target(open);
    if (session === undefined) {
      continue;
    }
    const paths = batches.get(session) ?? [];
    paths.push(open.path);
    batches.set(session, paths);
  }
  for (const [session, paths] of batches) {
    selectClientSession(session);
    void openFilesIn(session, paths);
  }
}

// Owned by its own root: a bare module-scope effect has no owner and never runs.
createRoot(() => createEffect(flush));

registerHostFeature((connection) =>
  connection.host
    .feature("files")
    .on<{ path: string; fallbackSlot: string | null }>("openPath", ({ path, fallbackSlot }) => {
      pending.push({ path, backendId: connection.id, fallbackSlot });
      flush();
    }),
);
