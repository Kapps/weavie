import type { CommandRunner } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { ContextMenuEntry, ContextMenuState } from "./ContextMenu";
import type { RailSession } from "./session-store";

/** The whole open menu for a session, named by its label (qualified by location when it isn't local). */
export function sessionMenuAt(
  session: RailSession,
  x: number,
  y: number,
  runCommand: CommandRunner,
): ContextMenuState {
  return {
    runCommand,
    x,
    y,
    header: session.isLocal ? session.label : `${session.label} @ ${session.locationName}`,
    entries: sessionMenuEntries(session),
  };
}

// The right-click entries for a session, command-driven. Every command carries the session's owning `backendId`,
// so a remote session's load / unload / delete runs on its own host. The session on the workspace's own checkout
// gets no Delete — the host refuses it — so unload is its only teardown.
export function sessionMenuEntries(session: RailSession): ContextMenuEntry[] {
  const args = { id: session.id, backendId: session.backendId };
  const entries: ContextMenuEntry[] = [
    session.loaded
      ? { commandId: CommandIds.unloadSession, args, label: "Unload session" }
      : { commandId: CommandIds.loadSession, args, label: "Load session" },
    { commandId: CommandIds.recreateSessionPrompt, args, label: "Recreate with…" },
  ];
  if (!session.workspaceCheckout) {
    entries.push(
      { kind: "separator" },
      { commandId: CommandIds.deleteSessionPrompt, args, label: "Delete…", danger: true },
    );
  }
  return entries;
}
