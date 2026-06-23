import { createSignal } from "solid-js";
import {
  connectBackend,
  connectedBackends,
  disconnectBackend,
  log,
  onSessionMessage,
  postToBackend,
} from "../bridge";

// A registered remote agent: a name plus how to reach its runner's control plane (the long-lived daemon on the
// remote box). The runner mints the actual worker {url, token}, resolved on connect. Persisted HOST-SIDE in
// ~/.weavie/remote-agents.json — the host owns persistence, the web owns the connections. (It used to live in
// localStorage, which the Debug dev server's per-launch origin silently orphaned on every restart.) See
// docs/specs/remote-sessions.md.
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
 * Connect + register a new agent. Validates by connecting first (throws on failure — surfaced in the Add modal,
 * so a bad agent is never persisted), then asks the local host to persist it. The host echoes the updated
 * registry back as a `remote-agents` push, which reconciles the reactive list. Replaces any agent of the same
 * name.
 */
export async function addAgent(agent: RemoteAgent): Promise<void> {
  await connectAgent(agent); // throws on failure → shown in the modal; a bad agent is never persisted
  postToBackend("local", {
    type: "add-remote-agent",
    name: agent.name,
    url: agent.url,
    token: agent.token,
  });
}

/**
 * Disconnect + forget a registered agent: close its bridge (so its chips leave the rail) and ask the local host
 * to drop it from the registry. The host echoes the updated list, reconciling the reactive set. Safe for an
 * agent that never connected (a down runner at startup) — disconnectBackend is a no-op then.
 */
export function removeAgent(name: string): void {
  disconnectBackend(agentBackendId(name));
  postToBackend("local", { type: "remove-remote-agent", name });
}

// The host pushes the persisted registry on `ready` and after any add/remove (from this or another window). We
// honor it only from the LOCAL backend — a remote runner pushes its OWN registry, which must not leak in — and
// reconcile our connections to match. Registered at module load, before main.tsx sends `ready`.
onSessionMessage((message, backendId) => {
  if (message.type === "remote-agents" && backendId === "local") {
    reconcile(message.agents);
  }
});

// Bring the live connections in line with the persisted registry: set the reactive list, connect any agent not
// yet connected (best-effort; a down runner just logs), and drop any remote backend whose agent is gone (e.g.
// removed in another window).
function reconcile(list: RemoteAgent[]): void {
  setAgents(list);
  const connected = new Set(connectedBackends().map((b) => b.id));
  const wanted = new Set(list.map((a) => agentBackendId(a.name)));
  for (const agent of list) {
    if (!connected.has(agentBackendId(agent.name))) {
      void connectAgent(agent).catch((err) => {
        log("error", `remote agent ${agent.name}: ${String(err)}`);
      });
    }
  }
  for (const backend of connectedBackends()) {
    if (!backend.isLocal && !wanted.has(backend.id)) {
      disconnectBackend(backend.id);
    }
  }
}

// Resolve the agent's worker bridge via the runner control plane, then wire it as a backend. The runner's
// GET /backend ensures the worker is up and returns its page URL with its token; the bridge WebSocket URL is
// derived from it. Throws on any failure so callers can decide whether to surface it (add) or swallow it
// (best-effort reconcile/reconnect).
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
