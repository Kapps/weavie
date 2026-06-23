import { CommandIds } from "../commands/types";
import type { ContextMenuEntry } from "./ContextMenu";
import type { RailSession } from "./session-store";

// The right-click entries for a session chip, shared by the rail and the cloud panel so both menus stay
// identical and command-driven. Every command carries the session's owning `backendId`, so a remote session's
// load / unload / delete runs on its own host. `inRail` adds "Remove from rail" for a remote (only meaningful
// once it's in the working set). The primary checkout has no worktree and gets no menu — callers skip it.
export function sessionMenuEntries(session: RailSession, inRail: boolean): ContextMenuEntry[] {
  const args = { id: session.id, backendId: session.backendId };
  const entries: ContextMenuEntry[] = [
    session.loaded
      ? { commandId: CommandIds.unloadSession, args, label: "Unload session" }
      : { commandId: CommandIds.loadSession, args, label: "Load session" },
    { kind: "separator" },
    { commandId: CommandIds.deleteSessionPrompt, args, label: "Delete…", danger: true },
  ];
  if (!session.isLocal && inRail) {
    entries.push(
      { kind: "separator" },
      {
        commandId: CommandIds.removeFromRail,
        args: { backendId: session.backendId, id: session.id },
        label: "Remove from rail",
      },
    );
  }
  return entries;
}
