import { createEffect, createRoot } from "solid-js";
import {
  type ClientSession,
  registerHostFeature,
  selectClientSession,
  selectedSession,
  sessionForSlot,
} from "../bridge";
import { chooseOpenSlot } from "./open-target";

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
  for (const open of pending.splice(0)) {
    const session = target(open);
    if (session === undefined) {
      continue;
    }
    // Selected first: the editor only activates a tab for the session in front, so an unselected target
    // would open the file where the user cannot see it.
    selectClientSession(session);
    session.feature("files").publish("reveal", { path: open.path, line: 1, preview: false });
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
