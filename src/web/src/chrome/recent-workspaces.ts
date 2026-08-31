import { createSignal } from "solid-js";
import { registerHostFeature } from "../bridge";

const [recentWorkspaces, setRecentWorkspaces] = createSignal<readonly string[]>(
  window.__WEAVIE_SHELL__?.recents ?? [],
);

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }
  return connection.host
    .feature("recentWorkspaces")
    .on<{ recents: string[] }>("changed", ({ recents }) => {
      setRecentWorkspaces(recents);
    });
});

export { recentWorkspaces };
