import { describe, expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "./bridge";
import type { KnownRepo, RepoState } from "./messaging/protocol-types";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

type Handler = (payload: unknown) => void;
const harness = vi.hoisted(() => ({
  install: (_connection: HostConnection): void => {},
}));

vi.mock("./bridge", () => ({
  registerHostFeature: (installer: (connection: HostConnection) => void) => {
    harness.install = installer;
  },
}));

const { knownRepos, onTestProfileChanged, repoStateOf } = await import("./repo-state");

// One fake host: `hello` delivers its hello, `push` one of its host events.
function connect(id: string) {
  const events = new Map<string, Handler>();
  let hello: (hello: { repos: RepoState[]; knownRepos: KnownRepo[] }) => void = () => {};
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
    hello: (repos: RepoState[], known: KnownRepo[]) => hello({ repos, knownRepos: known }),
    push: (event: string, payload: unknown) => events.get(event)?.(payload),
    session: (repo: string) => ({ connection: { id }, repo }) as unknown as ClientSession,
  };
}

describe("repo state", () => {
  it("offers each host's own repos, from its hello and then as the host changes them", () => {
    const host = connect("local");
    host.hello([], [{ repo: "/mono", label: "mono" }]);
    expect(knownRepos("local")).toEqual([{ repo: "/mono", label: "mono" }]);

    const offered = [
      { repo: "/mono", label: "mono" },
      { repo: "/infra", label: "infra" },
    ];
    host.push("repos.known", offered);
    expect(knownRepos("local")).toEqual(offered);
    expect(knownRepos("remote")).toEqual([]);
  });

  it("answers each session with its own repo's state, from the hello and then from each push", () => {
    const host = connect("local");
    host.hello(
      [
        { repo: "/mono", testProfile: "[]", suggestions: [], recentFiles: ["a.ts"] },
        { repo: "/infra", testProfile: "", suggestions: [], recentFiles: [] },
      ],
      [],
    );
    host.push("recentFiles.changed", { repo: "/infra", files: ["main.tf"] });

    expect(repoStateOf(host.session("/mono")).recentFiles).toEqual(["a.ts"]);
    expect(repoStateOf(host.session("/infra")).recentFiles).toEqual(["main.tf"]);
    expect(repoStateOf(connect("remote").session("/mono")).recentFiles).toEqual([]);
    expect(repoStateOf(null).suggestions).toEqual([]);
  });

  it("tells test-profile listeners only when a profile changes", () => {
    const host = connect("local");
    host.hello([{ repo: "/mono", testProfile: "", suggestions: [], recentFiles: [] }], []);
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
