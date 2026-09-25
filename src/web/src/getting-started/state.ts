import { createSignal } from "solid-js";
import { hostConnection, LOCAL_BACKEND_ID, registerHostFeature } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";

export const COMPLETED_SETTING = "gettingStarted.completed";

/** Whether the Getting Started modal is showing over the workspace. */
export const [gettingStartedOpen, setGettingStartedOpen] = createSignal(false);

registerCommand(CommandIds.gettingStarted, () => {
  setGettingStartedOpen(true);
});

// The host asks for setup on connect while it hasn't been finished or dismissed.
registerHostFeature((connection) =>
  connection.isLocal
    ? connection.host.feature("gettingStarted").on("show", () => {
        setGettingStartedOpen(true);
      })
    : undefined,
);

function settings() {
  const connection = hostConnection(LOCAL_BACKEND_ID);
  if (connection === undefined) throw new Error("The Weavie host is not connected.");
  return connection.host.feature("settings");
}

/** Reads one global setting's effective value from the local host. */
export async function readSetting<T>(key: string): Promise<T> {
  const setting = await settings().request<{ value: T }, { key: string }>("get", { key });
  return setting.value;
}

/** Writes one global setting on the local host; rejects when it's invalid or an env var overrides it. */
export function writeSetting(key: string, value: unknown): Promise<void> {
  return settings().request<void, { key: string; value: unknown }>("set", { key, value });
}
