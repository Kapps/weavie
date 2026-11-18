import { describe, expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "./bridge";
import type { RepoState } from "./messaging/protocol-types";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

type Handler = (payload: Record<string, unknown>) => void;
const harness = vi.hoisted(() => ({
  install: (_connection: HostConnection): void => {},
}));

vi.mock("./bridge", () => ({
  registerHostFeature: (installer: (connection: HostConnection) => void) => {
    harness.install = installer;
  },
}));

const { onTestProfileChanged, repoStateOf } = await import("./repo-state");

// One fake host: `hello` delivers its hello, `push` one of its host events.
function connect(id: string) {
  const events = new Map<string, Handler>();
  let hello: (hello: { repos: RepoState[] }) => void = () => {};
  harness.install({
    id,
    onHello: (listener: typeof hello) => {
      hello = listener;
      return () => {};
    },
    host: {
      feature: (feature: string) => ({
        on: (event: string, handler: Handler) => {
          events.set(`${feature}.${event}`, handler);
          return () => {};
        },
      }),
    },
  } as unknown as HostConnection);
  return {
    hello: (repos: RepoState[]) => hello({ repos }),
    push: (event: string, payload: Record<string, unknown>) => events.get(event)?.(payload),
    session: (repo: string) => ({ connection: { id }, repo }) as unknown as ClientSession,
  };
}

describe("repo state", () => {
  it("answers each session with its own repo's state, from the hello and then from each push", () => {
    const host = connect("local");
    host.hello([
      { repo: "/mono", testProfile: "[]", suggestions: [], recentFiles: ["a.ts"] },
      { repo: "/infra", testProfile: "", suggestions: [], recentFiles: [] },
    ]);
    host.push("recentFiles.changed", { repo: "/infra", files: ["main.tf"] });

    expect(repoStateOf(host.session("/mono")).recentFiles).toEqual(["a.ts"]);
    expect(repoStateOf(host.session("/infra")).recentFiles).toEqual(["main.tf"]);
    expect(repoStateOf(connect("remote").session("/mono")).recentFiles).toEqual([]);
    expect(repoStateOf(null).suggestions).toEqual([]);
  });

  it("tells test-profile listeners only when a profile changes", () => {
    const host = connect("local");
    host.hello([{ repo: "/mono", testProfile: "", suggestions: [], recentFiles: [] }]);
    const changed = vi.fn();
    const stop = onTestProfileChanged(changed);

    host.push("recentFiles.changed", { repo: "/mono", files: ["a.ts"] });
    host.push("suggestions.changed", { repo: "/mono", items: [] });
    host.push("tests.profile", { repo: "/mono", profile: "" });
    expect(changed).not.toHaveBeenCalled();

    host.push("tests.profile", { repo: "/mono", profile: "[]" });
    expect(changed).toHaveBeenCalledTimes(1);
    stop();
    host.push("tests.profile", { repo: "/mono", profile: "" });
    expect(changed).toHaveBeenCalledTimes(1);
  });
});
