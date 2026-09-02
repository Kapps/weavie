import { type ContextOverrides, evaluateWhen } from "../commands/context";
import { findCommand } from "../commands/registry";
import { CommandIds, type CommandInfo } from "../commands/types";
import type { ApplicationMenuEntry } from "./application-menu";
import type { ContextMenuEntry, ContextMenuItem } from "./ContextMenu";

function leaf(path: string): string {
  const parts = path.split(/[\\/]/).filter((part) => part.length > 0);
  return parts.length > 0 ? (parts[parts.length - 1] as string) : path;
}

function commandInfo(id: string): CommandInfo {
  const command = findCommand(id);
  if (command === undefined) {
    throw new Error(`Application menu references unknown command '${id}'.`);
  }
  return command;
}

function commandItem(
  id: string,
  context: ContextOverrides,
  args?: unknown,
  label?: string,
  title?: string,
): ContextMenuItem {
  const command = commandInfo(id);
  return {
    commandId: id,
    disabled: !evaluateWhen(command.when, context),
    ...(args === undefined ? {} : { args }),
    ...(label === undefined ? {} : { label }),
    ...(title === undefined ? {} : { title }),
  };
}

function normalize(entries: Array<ContextMenuEntry | null>): ContextMenuEntry[] {
  const normalized: ContextMenuEntry[] = [];
  for (const entry of entries) {
    if (entry === null) {
      continue;
    }
    if (entry.kind === "separator") {
      if (normalized.length === 0 || normalized[normalized.length - 1]?.kind === "separator") {
        continue;
      }
    }
    normalized.push(entry);
  }
  if (normalized[normalized.length - 1]?.kind === "separator") {
    normalized.pop();
  }
  return normalized;
}

/** Resolves one curated menu against the active catalog, context, platform, and recent workspaces. */
export function buildApplicationMenuEntries(
  definitions: ApplicationMenuEntry[],
  recents: readonly string[],
  platform: string,
  context: ContextOverrides,
): ContextMenuEntry[] {
  return normalize(
    definitions.map((definition): ContextMenuEntry | null => {
      if (definition.kind === "separator") {
        return { kind: "separator" };
      }
      if (definition.kind === "recentWorkspaces") {
        const command = commandInfo(CommandIds.openRecentWorkspace);
        if (!evaluateWhen(command.when, context)) {
          return null;
        }
        return {
          kind: "submenu",
          label: command.title,
          disabled: recents.length === 0,
          entries: recents.map((path) =>
            commandItem(CommandIds.openRecentWorkspace, context, { path }, leaf(path), path),
          ),
        };
      }
      if (definition.kind === "submenu") {
        const entries = buildApplicationMenuEntries(definition.entries, recents, platform, context);
        return {
          kind: "submenu",
          label: definition.label,
          entries,
          disabled: entries.every((entry) => entry.kind === "separator" || entry.disabled === true),
        };
      }
      if (definition.excludePlatforms?.includes(platform) === true) {
        return null;
      }
      const command = commandInfo(definition.commandId);
      if (command.when === "nativeShell" && !evaluateWhen(command.when, context)) {
        return null;
      }
      return commandItem(definition.commandId, context);
    }),
  );
}
