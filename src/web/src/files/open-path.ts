import { createEffect, createRoot } from "solid-js";
import { registerHostFeature, selectedSession } from "../bridge";
import { revealSelectedFile } from "./session-files";

// A path the OS handed a host — an "Open With", or `weavie <path>`. The host forwards it rather than opening
// it itself, because the session the user is looking at may belong to a different backend than the one the
// desktop launched. A cold launch arrives before anything is selected, so it waits for a session rather than
// being dropped into a page that has nowhere to put it.
const pending: string[] = [];

function flush(): void {
  if (selectedSession() === null) {
    return;
  }
  for (const path of pending.splice(0)) {
    revealSelectedFile(path, 1);
  }
}

// Owned by its own root: a bare module-scope effect has no owner and never runs.
createRoot(() => createEffect(flush));

registerHostFeature((connection) =>
  connection.host.feature("files").on<{ path: string }>("openPath", ({ path }) => {
    pending.push(path);
    flush();
  }),
);
