import { createSignal } from "solid-js";
import { type ClientSession, registerHostFeature, type Suggestion } from "./bridge";
import type { RepoState } from "./messaging/protocol-types";

const [states, setStates] = createSignal(new Map<string, RepoState>());
const profileListeners = new Set<() => void>();
const key = (backendId: string, repo: string): string => `${backendId}\n${repo}`;
const empty = (repo: string): RepoState => ({
  repo,
  testProfile: "",
  suggestions: [],
  recentFiles: [],
});

// Each host sends every repo's state in its hello, then each value under the repo's root as it changes.
registerHostFeature((connection) => {
  const apply = (repo: string, change: Partial<RepoState>): void => {
    const id = key(connection.id, repo);
    const before = states().get(id) ?? empty(repo);
    setStates((previous) => new Map(previous).set(id, { ...before, ...change }));
    if (change.testProfile !== undefined && change.testProfile !== before.testProfile) {
      for (const listener of profileListeners) {
        listener();
      }
    }
  };
  const stops = [
    connection.onHello((hello) => {
      for (const state of hello.repos) {
        apply(state.repo, state);
      }
    }),
    connection.host
      .feature("tests")
      .on<{ repo: string; profile: string }>("profile", ({ repo, profile }) =>
        apply(repo, { testProfile: profile }),
      ),
    connection.host
      .feature("suggestions")
      .on<{ repo: string; items: Suggestion[] }>("changed", ({ repo, items }) =>
        apply(repo, { suggestions: items }),
      ),
    connection.host
      .feature("recentFiles")
      .on<{ repo: string; files: string[] }>("changed", ({ repo, files }) =>
        apply(repo, { recentFiles: files }),
      ),
  ];
  return () => {
    for (const stop of stops) {
      stop();
    }
  };
});

/** The state of the repo a session's checkout belongs to; empty for no session. */
export function repoStateOf(session: ClientSession | null): RepoState {
  return (
    (session === null ? undefined : states().get(key(session.connection.id, session.repo))) ??
    empty(session?.repo ?? "")
  );
}

/** Subscribes to any repo's test profile changing. Returns an unsubscribe. Use outside a Solid reactive root. */
export function onTestProfileChanged(listener: () => void): () => void {
  profileListeners.add(listener);
  return () => {
    profileListeners.delete(listener);
  };
}
