import { type Accessor, createMemo, untrack } from "solid-js";
import type { ClientSession } from "../bridge";

/** One opening of Sessions: the session it was opened from and the location it preselects. */
export interface SessionsOpening {
  owner: ClientSession | null;
  backendId: string;
}

/** A new opening each time Sessions opens or its location changes while open; held while it stays open. */
export function createSessionsOpening(
  active: Accessor<boolean>,
  backendId: Accessor<string>,
  shown: Accessor<ClientSession | null>,
): Accessor<SessionsOpening> {
  return createMemo<SessionsOpening>(
    (held) => {
      const location = backendId();
      return active() ? { owner: untrack(shown), backendId: location } : held;
    },
    { owner: null, backendId: untrack(backendId) },
  );
}
