import { hostConnection, LOCAL_BACKEND_ID } from "./bridge";

/** One of the local host's features, for request/response calls. */
export function localFeature(name: string) {
  const connection = hostConnection(LOCAL_BACKEND_ID);
  if (connection === undefined) throw new Error("The Weavie host is not connected.");
  return connection.host.feature(name);
}

const settings = () => localFeature("settings");

/** Reads one global setting's effective value from the local host, and whether it's still the default. */
export async function readSetting<T>(key: string): Promise<{ value: T; isDefault: boolean }> {
  const setting = await settings().request<{ value: T; source: string }, { key: string }>("get", {
    key,
  });
  return { value: setting.value, isDefault: setting.source === "default" };
}

/** Writes one global setting on the local host; rejects when it's invalid or an env var overrides it. */
export function writeSetting(key: string, value: unknown): Promise<void> {
  return settings().request<void, { key: string; value: unknown }>("set", { key, value });
}
