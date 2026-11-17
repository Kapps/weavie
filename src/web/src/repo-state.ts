import { createSignal } from "solid-js";
import { type ClientSession, registerHostFeature, type Suggestion } from "./bridge";
import type { RepoState } from "./messaging/protocol-types";

const [states, setStates] = createSignal(new Map<string, RepoState>());
const listeners = new Set<() => void>();
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
    setStates((previous) =>
      new Map(previous).set(id, { ...(previous.get(id) ?? empty(repo)), ...change }),
    );
    for (const listener of listeners) {
      listener();
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

/** Subscribes to any repo's state changing. Returns an unsubscribe. Use outside a Solid reactive root. */
export function onRepoStateChanged(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
