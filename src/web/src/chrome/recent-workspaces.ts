import { createSignal } from "solid-js";
import { registerHomeFeature } from "../bridge";

const [recentWorkspaces, setRecentWorkspaces] = createSignal<readonly string[]>(
  window.__WEAVIE_SHELL__?.recents ?? [],
);

registerHomeFeature((connection) => {
  return connection.host
    .feature("recentWorkspaces")
    .on<{ recents: string[] }>("changed", ({ recents }) => {
      setRecentWorkspaces(recents);
    });
});

export { recentWorkspaces };
