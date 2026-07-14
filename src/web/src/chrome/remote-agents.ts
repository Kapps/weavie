import { createSignal } from "solid-js";
import {
  type BackendEndpoint,
  connectBackend,
  connectedBackends,
  disconnectBackend,
  onSessionMessage,
  postToLocalHost,
} from "../bridge";
import { notify } from "../notify/notify";

// A registered remote agent: a name plus how to reach its runner's control plane. The runner mints the actual
// worker {url, token}, resolved on connect. Persisted host-side in ~/.weavie/remote-agents.json (the host owns
// persistence, the web owns the connections). See docs/specs/remote-sessions.md.
export interface RemoteAgent {
  name: string;
  url: string; // the runner control-plane base URL (e.g. http://<tailscale-host>:8800)
  token: string; // the runner token
}

const [agents, setAgents] = createSignal<RemoteAgent[]>([]);

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
 * Connect + register a new agent. Validates the handshake up front (throws on failure, so a bad agent is never
 * persisted or connected), then wires the live connection and asks the host to persist it; the host echoes the
 * registry back. Replaces a same-named agent.
 */
export async function addAgent(agent: RemoteAgent): Promise<void> {
  await resolveWorkerBridge(agent);
  connectAgent(agent);
  postToLocalHost({
    type: "add-remote-agent",
    name: agent.name,
    url: agent.url,
    token: agent.token,
  });
}

/**
 * Disconnect + forget a registered agent: close its bridge and ask the host to drop it from the registry.
 * Safe for an agent that never connected — disconnectBackend is a no-op then.
 */
export function removeAgent(name: string): void {
  disconnectBackend(agentBackendId(name));
  postToLocalHost({ type: "remove-remote-agent", name });
  notify("info", `Disconnected remote agent "${name}".`);
}

// Honored only from the LOCAL backend — a remote runner pushes its own registry, which must not leak in. The
// host pushes on `ready` and after any add/remove. Registered at module load, before main.tsx sends `ready`.
onSessionMessage((message, backendId) => {
  if (message.type === "remote-agents" && backendId === "local") {
    reconcile(message.agents);
  }
});

// Bring live connections in line with the persisted registry: connect any agent not yet connected, and drop
// any remote backend whose agent is gone. A down runner needs no special-casing — the transport keeps retrying
// (with backoff) and connects when it comes up, so a not-yet-reachable agent self-heals without an app restart.
function reconcile(list: RemoteAgent[]): void {
  setAgents(list);
  const connected = new Set(connectedBackends().map((b) => b.id));
  const wanted = new Set(list.map((a) => agentBackendId(a.name)));
  for (const agent of list) {
    if (!connected.has(agentBackendId(agent.name))) {
      connectAgent(agent);
    }
  }
  for (const backend of connectedBackends()) {
    if (!backend.isLocal && !wanted.has(backend.id)) {
      disconnectBackend(backend.id);
    }
  }
}

// Wire the agent as a backend with a resolver that (re-)runs the runner handshake on every connect attempt, so
// it connects when the runner is up, retries (with backoff) when it isn't, and follows a restarted runner to its
// fresh worker port+token rather than a URL cached once. Idempotent (connectBackend no-ops if already wired).
function connectAgent(agent: RemoteAgent): void {
  connectBackend(agentBackendId(agent.name), agent.name, () => resolveWorkerBridge(agent));
}

// Resolve the agent's CURRENT worker bridge/resource URLs via the runner control plane (GET /backend ensures the worker
// is up and returns its page URL + token). Throws on any failure so the transport treats it as a drop and
// retries with backoff, following the runner back up after a restart.
async function resolveWorkerBridge(agent: RemoteAgent): Promise<BackendEndpoint> {
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
  return pageUrlToBackendEndpoint(body.url);
}

// The runner returns the worker's page URL (http://host:port/?token=T); the bridge lives at /weavie-bridge on
// the same host, gated by the same token.
function pageUrlToBackendEndpoint(pageUrl: string): BackendEndpoint {
  const u = new URL(pageUrl);
  const scheme = u.protocol === "https:" ? "wss:" : "ws:";
  const token = u.searchParams.get("token");
  const query = token === null ? "" : `?token=${encodeURIComponent(token)}`;
  return {
    bridgeUrl: `${scheme}//${u.host}/weavie-bridge${query}`,
    resourceBase: `${u.protocol}//${u.host}/weavie-media${query}`,
  };
}
