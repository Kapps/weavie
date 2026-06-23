import { createSignal } from "solid-js";
import { connectBackend, disconnectBackend, log } from "../bridge";

// A registered remote agent: a name plus how to reach its runner's control plane (the long-lived daemon on
// the remote box). The runner mints the actual worker {url, token}, resolved on connect. Stored client-side
// in localStorage. See docs/specs/remote-sessions.md.
export interface RemoteAgent {
  name: string;
  url: string; // the runner control-plane base URL (e.g. http://<tailscale-host>:8800)
  token: string; // the runner token
}

const STORAGE_KEY = "weavie.remoteAgents";

function load(): RemoteAgent[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw === null) {
      return [];
    }
    const parsed = JSON.parse(raw) as RemoteAgent[];
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

const [agents, setAgents] = createSignal<RemoteAgent[]>(load());

/** The registered remote agents (reactive), for the location picker. */
export const remoteAgents = agents;

/** A stable backend id for an agent (also the rail/location key). */
export function agentBackendId(name: string): string {
  return `remote:${name}`;
}

/** A deterministic hue (0-359) for an agent's identity colour, mirroring how a session's hue is derived. */
export function agentHue(name: string): number {
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = (hash * 31 + name.charCodeAt(i)) >>> 0;
  }
  return hash % 360;
}

/**
 * Connect + register a new agent, persisting it only once the connection succeeds. Replaces any agent with
 * the same name. Throws if the runner is unreachable or rejects us, so the caller (the Add modal) can surface
 * the failure instead of silently leaving the agent out of the location picker.
 */
export async function addAgent(agent: RemoteAgent): Promise<void> {
  await connectAgent(agent); // throws on failure → shown in the modal; a bad agent is never persisted
  const next = [...agents().filter((a) => a.name !== agent.name), agent];
  setAgents(next);
  localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
}

/**
 * Disconnect + forget a registered agent: close its bridge (so its chips leave the rail), drop it from the
 * reactive list, and remove it from localStorage. Safe for an agent that never connected (a down runner at
 * startup) — disconnectBackend is a no-op then, and we still forget it.
 */
export function removeAgent(name: string): void {
  disconnectBackend(agentBackendId(name));
  const next = agents().filter((a) => a.name !== name);
  setAgents(next);
  localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
}

/** Connect to all stored agents on startup (best-effort; a down runner just logs and is skipped). */
export function connectStoredAgents(): void {
  for (const agent of agents()) {
    void connectAgent(agent).catch((err) => {
      log("error", `remote agent ${agent.name}: ${String(err)}`);
    });
  }
}

// The New Session location picker remembers the last backend a session was created on (local or an agent id),
// so Ctrl+Shift+N defaults to where you last worked instead of resetting to local each time.
const LAST_LOCATION_KEY = "weavie.lastLocation";

/** The backend id the last session was created on (defaults to "local"). The caller validates it still exists. */
export function loadLastLocation(): string {
  try {
    return localStorage.getItem(LAST_LOCATION_KEY) ?? "local";
  } catch {
    return "local";
  }
}

/** Remember the backend id a session was just created on (or an agent just added), for the next prompt. */
export function saveLastLocation(backendId: string): void {
  localStorage.setItem(LAST_LOCATION_KEY, backendId);
}

// Resolve the agent's worker bridge via the runner control plane, then wire it as a backend. The runner's
// GET /backend ensures the worker is up and returns its page URL with its token; the bridge WebSocket URL is
// derived from it. Throws on any failure so callers can decide whether to surface it (add) or swallow it
// (best-effort startup reconnect).
async function connectAgent(agent: RemoteAgent): Promise<void> {
  const base = agent.url.replace(/\/+$/, "");
  let res: Response;
  try {
    res = await fetch(`${base}/backend`, {
      headers: { Authorization: `Bearer ${agent.token}` },
    });
  } catch (err) {
    throw new Error(`runner unreachable at ${base} (${String(err)})`);
  }
  if (!res.ok) {
    throw new Error(`runner returned ${res.status} for GET /backend`);
  }
  const body = (await res.json()) as { url?: string };
  if (typeof body.url !== "string") {
    throw new Error("runner /backend returned no worker url");
  }
  connectBackend(agentBackendId(agent.name), agent.name, pageUrlToBridgeWs(body.url));
  log("info", `remote agent ${agent.name}: connected`);
}

// The runner returns the worker's page URL (http://host:port/?token=T); the bridge lives at /weavie-bridge on
// the same host, gated by the same token.
function pageUrlToBridgeWs(pageUrl: string): string {
  const u = new URL(pageUrl);
  const scheme = u.protocol === "https:" ? "wss:" : "ws:";
  const token = u.searchParams.get("token");
  const query = token === null ? "" : `?token=${encodeURIComponent(token)}`;
  return `${scheme}//${u.host}/weavie-bridge${query}`;
}
