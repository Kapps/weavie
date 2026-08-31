import { describe, expect, it } from "vitest";
import { CommandIds } from "../commands/types";
import { APPLICATION_MENUS, applicationMenuCommandIds } from "./application-menu";

describe("application menu", () => {
  it("keeps the cross-platform top level compact and ordered", () => {
    expect(APPLICATION_MENUS.map((menu) => menu.label)).toEqual([
      "File",
      "Go",
      "View",
      "Diff",
      "Run",
    ]);
  });

  it("surfaces the core discovery workflows", () => {
    const ids = applicationMenuCommandIds();
    expect(ids).toEqual(
      expect.arrayContaining([
        CommandIds.openFolder,
        CommandIds.openRecentWorkspace,
        CommandIds.focusOmnibarCommands,
        CommandIds.diffAgainst,
        CommandIds.diffAgainstHead,
        CommandIds.diffAgainstParent,
        CommandIds.runTestsInFile,
        CommandIds.newTerminal,
        CommandIds.showSessions,
      ]),
    );
  });

  it("declares every command once", () => {
    const ids = applicationMenuCommandIds();
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("groups terminal and agent actions into submenus", () => {
    const run = APPLICATION_MENUS.find((menu) => menu.id === "run");
    const groups = run?.entries.flatMap((entry) => (entry.kind === "submenu" ? [entry.label] : []));
    expect(groups).toEqual(["Terminal", "Agent"]);
  });
});
