import { createEffect, createRoot, on } from "solid-js";
import { log, registerHostFeature } from "../bridge";
import { type ContextOverrides, onContextChanged, paneFocusContext } from "../commands/context";
import { findCommand, onCommandsChanged, runCommandWithFeedback } from "../commands/registry";
import { notify } from "../notify/notify";
import { APPLICATION_MENUS } from "./application-menu";
import { buildApplicationMenuEntries } from "./application-menu-model";
import type { ContextMenuEntry } from "./ContextMenu";
import { recentWorkspaces } from "./recent-workspaces";

export interface NativeApplicationMenuEntry {
  kind: "command" | "separator" | "submenu";
  label: string;
  enabled: boolean;
  token: string;
  keys: string[];
  toolTip?: string;
  entries: NativeApplicationMenuEntry[];
}

export interface NativeApplicationMenuState {
  revision: number;
  menus: Array<{ label: string; entries: NativeApplicationMenuEntry[] }>;
}

interface NativeMenuInvocation {
  commandId: string;
  args: unknown;
}

interface NativeMenuBuild {
  state: NativeApplicationMenuState;
  invocations: Map<string, NativeMenuInvocation>;
}

function nativeEntries(
  entries: ContextMenuEntry[],
  path: string,
  invocations: Map<string, NativeMenuInvocation>,
): NativeApplicationMenuEntry[] {
  return entries.map((entry, index) => {
    const token = `${path}/${index}`;
    if (entry.kind === "separator") {
      return {
        kind: "separator",
        label: "",
        enabled: false,
        token: "",
        keys: [],
        entries: [],
      };
    }
    if (entry.kind === "submenu") {
      return {
        kind: "submenu",
        label: entry.label,
        enabled: entry.disabled !== true,
        token: "",
        keys: [],
        entries: nativeEntries(entry.entries, token, invocations),
      };
    }
    const command = findCommand(entry.commandId);
    if (command === undefined) {
      throw new Error(`Application menu references unknown command '${entry.commandId}'.`);
    }
    invocations.set(token, { commandId: entry.commandId, args: entry.args });
    return {
      kind: "command",
      label: entry.label ?? command.title,
      enabled: entry.disabled !== true,
      token,
      keys: command.keys,
      ...(entry.title === undefined ? {} : { toolTip: entry.title }),
      entries: [],
    };
  });
}

/** Builds the native representation and its opaque activation table from the current web-owned menu model. */
export function buildNativeApplicationMenu(
  revision: number,
  platform: string,
  context: ContextOverrides,
  recents: readonly string[],
): NativeMenuBuild {
  const invocations = new Map<string, NativeMenuInvocation>();
  return {
    state: {
      revision,
      menus: APPLICATION_MENUS.map((menu) => ({
        label: menu.label,
        entries: nativeEntries(
          buildApplicationMenuEntries(menu.entries, recents, platform, context),
          menu.id,
          invocations,
        ),
      })),
    },
    invocations,
  };
}

/** Connects the macOS native application menu to the active web command catalog and dispatcher. */
export function installNativeApplicationMenu(): () => void {
  if (window.__WEAVIE_SHELL__?.platform !== "mac") {
    return () => {};
  }

  let revision = 0;
  let activeRevision = 0;
  let invocations = new Map<string, NativeMenuInvocation>();
  let publishToHost: ((state: NativeApplicationMenuState) => void) | null = null;
  let publishQueued = false;
  let disposed = false;
  const publish = (): void => {
    if (publishToHost === null) {
      return;
    }
    const built = buildNativeApplicationMenu(
      ++revision,
      "mac",
      paneFocusContext(document.activeElement),
      recentWorkspaces(),
    );
    activeRevision = built.state.revision;
    invocations = built.invocations;
    publishToHost(built.state);
  };
  const schedulePublish = (): void => {
    if (publishQueued) {
      return;
    }
    publishQueued = true;
    queueMicrotask(() => {
      publishQueued = false;
      if (!disposed) {
        publish();
      }
    });
  };

  const stopHost = registerHostFeature((connection) => {
    if (!connection.isLocal) {
      return;
    }
    const feature = connection.host.feature("applicationMenu");
    publishToHost = (state) => feature.publish("state", state);
    const stopInvocations = feature.on<{ revision: number; token: string }>(
      "invoke",
      ({ revision: invokedRevision, token }) => {
        const invocation = invocations.get(token);
        if (invokedRevision !== activeRevision || invocation === undefined) {
          log("warn", "ignored activation from a superseded native application menu");
          notify(
            "warn",
            "The application menu changed. Open it again to choose the refreshed item.",
            "native-application-menu-stale",
          );
          return;
        }
        void runCommandWithFeedback(invocation.commandId, invocation.args);
      },
    );
    publish();
    return () => {
      stopInvocations();
      publishToHost = null;
    };
  });
  const stopCommands = onCommandsChanged(schedulePublish);
  const stopContext = onContextChanged(schedulePublish);
  const stopRecents = createRoot((dispose) => {
    createEffect(on(recentWorkspaces, schedulePublish, { defer: true }));
    return dispose;
  });

  return () => {
    disposed = true;
    stopRecents();
    stopContext();
    stopCommands();
    stopHost();
  };
}
