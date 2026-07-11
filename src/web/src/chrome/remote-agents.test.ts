import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const calls = vi.hoisted(() => ({
  connect: [] as Array<[string, string, () => Promise<string>]>,
  disconnect: [] as string[],
  posted: [] as Array<Record<string, unknown>>,
}));
vi.mock("../bridge", () => ({
  connectBackend: (id: string, name: string, resolveUrl: () => Promise<string>) =>
    calls.connect.push([id, name, resolveUrl]),
  disconnectBackend: (id: string) => calls.disconnect.push(id),
  connectedBackends: () => [],
  onSessionMessage: () => () => {},
  postToLocalHost: (message: Record<string, unknown>) => calls.posted.push(message),
}));

const agents = await import("./remote-agents");

beforeEach(() => {
  calls.connect.length = 0;
  calls.disconnect.length = 0;
  calls.posted.length = 0;
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe("agentBackendId", () => {
  it("namespaces the agent name under remote:", () => {
    expect(agents.agentBackendId("bob")).toBe("remote:bob");
  });
});

describe("agentHue", () => {
  it("is deterministic and within 0..359", () => {
    const h = agents.agentHue("alice");
    expect(h).toBe(agents.agentHue("alice"));
    expect(h).toBeGreaterThanOrEqual(0);
    expect(h).toBeLessThan(360);
  });

  it("distinguishes different names", () => {
    expect(agents.agentHue("alice")).not.toBe(agents.agentHue("bob"));
  });
});

describe("addAgent", () => {
  it("connects the worker bridge and persists the agent on success", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => ({
        ok: true,
        json: async () => ({ url: "https://host:9/?token=abc" }),
      })),
    );
    await agents.addAgent({ name: "bob", url: "https://runner:8800/", token: "t" });
    expect(calls.connect).toHaveLength(1);
    expect(calls.connect[0]?.[0]).toBe("remote:bob");
    // The transport gets a resolver, not a fixed URL, so a reconnect re-runs the runner handshake. It derives
    // the bridge WS from the worker page URL, carrying the token.
    expect(await calls.connect[0]?.[2]()).toBe("wss://host:9/weavie-bridge?token=abc");
    expect(calls.posted).toContainEqual({
      type: "add-remote-agent",
      name: "bob",
      url: "https://runner:8800/",
      token: "t",
    });
  });

  it("hands the transport a resolver that follows a restarted runner to its fresh worker url+token", async () => {
    // A runner restart mints a new worker port+token; each /backend fetch advertises the current one.
    const advertised = ["https://host:9/?token=old", "https://host:10/?token=new"];
    let call = 0;
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => ({
        ok: true,
        json: async () => ({ url: advertised[Math.min(call++, advertised.length - 1)] }),
      })),
    );
    await agents.addAgent({ name: "bob", url: "https://runner:8800", token: "t" });
    const resolve = calls.connect[0]?.[2];
    // addAgent's up-front validation consumed the first advertisement; the resolver the transport reconnects
    // through re-fetches and picks up whatever the runner advertises now — the new port+token, not the stale one.
    expect(await resolve?.()).toBe("wss://host:10/weavie-bridge?token=new");
  });

  it("rejects and never persists when the runner is unreachable", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        throw new Error("ECONNREFUSED");
      }),
    );
    await expect(
      agents.addAgent({ name: "bob", url: "https://runner:8800", token: "t" }),
    ).rejects.toThrow(/unreachable/);
    expect(calls.posted).toEqual([]);
    expect(calls.connect).toEqual([]);
  });

  it("rejects and never persists when the runner returns a non-OK status", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => ({ ok: false, status: 403, json: async () => ({}) })),
    );
    await expect(
      agents.addAgent({ name: "bob", url: "https://runner:8800", token: "t" }),
    ).rejects.toThrow(/403/);
    expect(calls.posted).toEqual([]);
  });
});

describe("removeAgent", () => {
  it("disconnects the backend and asks the host to forget it", () => {
    agents.removeAgent("bob");
    expect(calls.disconnect).toContain("remote:bob");
    expect(calls.posted).toContainEqual({ type: "remove-remote-agent", name: "bob" });
  });
});
