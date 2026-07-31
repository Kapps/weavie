import { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

const runtime = vi.hoisted(() => ({
  sessions: new Map<string, ClientSession>(),
}));

vi.mock("../bridge", () => ({
  clientSessionAt: (
    backend: string,
    address: { slot: string; incarnation: string },
  ): ClientSession | undefined =>
    runtime.sessions.get(`${backend}\0${address.slot}\0${address.incarnation}`),
}));

const { hostUriString, protocolUri, sessionFileUri, sessionForUri, sessionUriHostPath } =
  await import("./session-uri");

function session(backend: string, slot: string, incarnation: string): ClientSession {
  const value = {
    connection: { id: backend },
    address: { slot, incarnation },
  } as ClientSession;
  runtime.sessions.set(`${backend}\0${slot}\0${incarnation}`, value);
  return value;
}

beforeEach(() => runtime.sessions.clear());

describe("session file URIs", () => {
  it("uses a canonicalization-safe session namespace without changing the host path", () => {
    const owner = session("local", "primary", "inc-1");
    const uri = sessionFileUri(owner, "/worktree/src/app.ts");

    expect(uri.scheme).toBe("weavie-file");
    expect(uri.path).toMatch(/^\/weavie-session-\d+\/worktree\/src\/app\.ts$/);
    expect(sessionForUri(uri)).toBe(owner);
    expect(sessionUriHostPath(uri)).toBe("/worktree/src/app.ts");
  });

  it("gives equal host paths distinct model identities and restores protocol URIs exactly", () => {
    const first = session("local", "one", "inc-1");
    const second = session("remote", "one", "inc-2");
    const firstUri = sessionFileUri(first, "/worktree/app.ts");
    const secondUri = sessionFileUri(second, "/worktree/app.ts");

    expect(firstUri.toString()).not.toBe(secondUri.toString());
    expect(sessionUriHostPath(firstUri)).toBe("/worktree/app.ts");
    expect(sessionUriHostPath(secondUri)).toBe("/worktree/app.ts");

    const protocol = URI.parse("file://server/share/app.ts?version=1#symbol");
    const namespaced = protocolUri(first, protocol.toString());
    expect(hostUriString(namespaced)).toBe("file://server/share/app.ts?version%3D1#symbol");
  });
});
