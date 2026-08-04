import { describe, expect, it } from "vitest";
import { routeCommandCatalog } from "./catalog-routing";
import type { CommandInfo, ResolvedKeybinding } from "./types";

function command(id: string, target: "pageHost" | "sessionHost"): CommandInfo {
  return {
    id,
    title: id,
    runsIn: "core",
    target,
    description: "",
    aliases: [],
    showInPalette: true,
    keys: [],
  };
}

function binding(key: string, id: string): ResolvedKeybinding {
  return { key, command: id };
}

function legacyCommand(id: string): CommandInfo {
  return {
    id,
    title: id,
    runsIn: "core",
    description: "",
    aliases: [],
    showInPalette: true,
    keys: [],
  } as unknown as CommandInfo;
}

describe("routeCommandCatalog", () => {
  it("uses local page-host entries and remote session-host entries", () => {
    const routed = routeCommandCatalog(
      "local",
      "remote:r",
      {
        commands: [
          command("theme.cycle", "pageHost"),
          command("font.increase", "pageHost"),
          command("terminal.reopen", "sessionHost"),
        ],
        keybindings: [
          binding("ctrl+m", "theme.cycle"),
          binding("ctrl+=", "font.increase"),
          binding("ctrl+t", "terminal.reopen"),
        ],
      },
      {
        commands: [
          command("theme.cycle", "pageHost"),
          command("terminal.reopen", "sessionHost"),
          legacyCommand("legacy.terminal"),
          command("remote.window.action", "pageHost"),
        ],
        keybindings: [
          binding("alt+m", "theme.cycle"),
          binding("alt+t", "terminal.reopen"),
          binding("alt+l", "legacy.terminal"),
          binding("alt+w", "remote.window.action"),
        ],
      },
    );

    expect(
      routed.commands.map(({ catalogBackendId, command: item }) => [catalogBackendId, item.id]),
    ).toEqual([
      ["local", "theme.cycle"],
      ["remote:r", "terminal.reopen"],
      ["remote:r", "legacy.terminal"],
      ["local", "font.increase"],
    ]);
    expect(routed.keybindings).toEqual([
      { catalogBackendId: "remote:r", binding: binding("alt+t", "terminal.reopen") },
      { catalogBackendId: "remote:r", binding: binding("alt+l", "legacy.terminal") },
      { catalogBackendId: "local", binding: binding("ctrl+m", "theme.cycle") },
      { catalogBackendId: "local", binding: binding("ctrl+=", "font.increase") },
    ]);
  });

  it("keeps the local catalog unchanged when the local backend is active", () => {
    const local = {
      commands: [command("theme.cycle", "pageHost")],
      keybindings: [binding("ctrl+m", "theme.cycle")],
    };

    expect(routeCommandCatalog("local", "local", local, local)).toEqual({
      commands: [{ catalogBackendId: "local", command: local.commands[0] }],
      keybindings: [{ catalogBackendId: "local", binding: local.keybindings[0] }],
    });
  });
});
