import { createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature } from "../bridge";

export interface PullRequestStatus {
  branch: string | null;
  pullRequest: {
    number: number;
    url: string;
    state: "open" | "merged" | "closed";
  } | null;
  error: string | null;
}

const [statuses, setStatuses] = createSignal(new Map<ClientSession, PullRequestStatus>());

export function pullRequestStatus(session: ClientSession | null): PullRequestStatus | null {
  return session === null || session.closed ? null : (statuses().get(session) ?? null);
}

registerSessionFeature((session) => {
  const off = session.feature("git").on<PullRequestStatus>("pullRequest", (status) => {
    setStatuses((previous) => new Map(previous).set(session, status));
  });
  return () => {
    off();
    setStatuses((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});
