import type { ClientSession } from "../bridge";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import { canonicalFsPath } from "./fs-path";

const modes = createSessionOwnedState<ReadonlySet<string>>(() => new Set());

export function isPreviewMode(session: ClientSession, path: string): boolean {
  return modes.get(session)?.has(canonicalFsPath(path)) ?? false;
}

export function toggleViewMode(session: ClientSession, path: string): "source" | "preview" {
  const key = canonicalFsPath(path);
  const next = new Set(modes.get(session));
  const preview = !next.has(key);
  if (preview) next.add(key);
  else next.delete(key);
  modes.update(session, () => next);
  return preview ? "preview" : "source";
}
