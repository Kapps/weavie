import { createSignal } from "solid-js";
import { connectBackend, log } from "../bridge";

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

/** Persist + register a new agent, then connect to it. Replaces any agent with the same name. */
export async function addAgent(agent: RemoteAgent): Promise<void> {
  const next = [...agents().filter((a) => a.name !== agent.name), agent];
  setAgents(next);
  localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
  await connectAgent(agent);
}

/** Connect to all stored agents on startup (best-effort; a down runner just logs and is skipped). */
export function connectStoredAgents(): void {
  for (const agent of agents()) {
    void connectAgent(agent);
  }
}

// Resolve the agent's worker bridge via the runner control plane, then wire it as a backend. The runner's
// GET /backend ensures the worker is up and returns its page URL with its token; the bridge WebSocket URL is
// derived from it.
async function connectAgent(agent: RemoteAgent): Promise<void> {
  const base = agent.url.replace(/\/+$/, "");
  try {
    const res = await fetch(`${base}/backend`, {
      headers: { Authorization: `Bearer ${agent.token}` },
    });
    if (!res.ok) {
      log("error", `remote agent ${agent.name}: GET /backend → ${res.status}`);
      return;
    }
    const body = (await res.json()) as { url?: string };
    if (typeof body.url !== "string") {
      log("error", `remote agent ${agent.name}: /backend returned no url`);
      return;
    }
    connectBackend(agentBackendId(agent.name), agent.name, pageUrlToBridgeWs(body.url));
    log("info", `remote agent ${agent.name}: connected`);
  } catch (err) {
    log("error", `remote agent ${agent.name}: unreachable (${String(err)})`);
  }
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
