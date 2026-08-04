import type { CommandInfo, ResolvedKeybinding } from "./types";

export interface CommandCatalogSnapshot {
  commands: CommandInfo[];
  keybindings: ResolvedKeybinding[];
}

export interface RoutedCommand {
  catalogBackendId: string;
  command: CommandInfo;
}

export interface RoutedKeybinding {
  catalogBackendId: string;
  binding: ResolvedKeybinding;
}

export interface RoutedCommandCatalog {
  commands: RoutedCommand[];
  keybindings: RoutedKeybinding[];
}

/** Resolves page-host commands from the local catalog and session-host commands from the active backend. */
export function routeCommandCatalog(
  localBackendId: string,
  activeBackendId: string,
  local: CommandCatalogSnapshot,
  active: CommandCatalogSnapshot,
): RoutedCommandCatalog {
  if (activeBackendId === localBackendId) {
    return {
      commands: local.commands.map((command) => ({ catalogBackendId: localBackendId, command })),
      keybindings: local.keybindings.map((binding) => ({
        catalogBackendId: localBackendId,
        binding,
      })),
    };
  }

  const pageHostCommands = new Map(
    local.commands
      .filter((command) => command.target === "pageHost")
      .map((command) => [command.id, command]),
  );
  const seen = new Set<string>();
  const commands: RoutedCommand[] = [];
  for (const command of active.commands) {
    const localCommand = pageHostCommands.get(command.id);
    if (localCommand !== undefined) {
      seen.add(command.id);
      commands.push({ catalogBackendId: localBackendId, command: localCommand });
    } else if (command.target !== "pageHost") {
      seen.add(command.id);
      commands.push({ catalogBackendId: activeBackendId, command });
    }
  }
  for (const command of pageHostCommands.values()) {
    if (!seen.has(command.id)) {
      commands.push({ catalogBackendId: localBackendId, command });
    }
  }

  const pageHostIds = new Set(pageHostCommands.keys());
  const remotePageHostIds = new Set(
    active.commands.filter((command) => command.target === "pageHost").map((command) => command.id),
  );
  return {
    commands,
    keybindings: [
      ...active.keybindings
        .filter(
          (binding) => !pageHostIds.has(binding.command) && !remotePageHostIds.has(binding.command),
        )
        .map((binding) => ({ catalogBackendId: activeBackendId, binding })),
      ...local.keybindings
        .filter((binding) => pageHostIds.has(binding.command))
        .map((binding) => ({ catalogBackendId: localBackendId, binding })),
    ],
  };
}
