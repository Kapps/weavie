import { registerHostFeature } from "../bridge";
import { keyHintInCatalog } from "../commands/key-hint";
import {
  findCommandInCatalog,
  onCommandsChanged,
  runCommandFromCatalogWithFeedback,
} from "../commands/registry";
import { notify } from "../notify/notify";
import type { ToastAction } from "../notify/Toasts";
import { onSplashDismissed } from "../splash";

interface StartupTip {
  id: string;
  lead: string;
  commandId: string | null;
  detail: string;
}

function message(backendId: string, tip: StartupTip): string {
  const shortcut = tip.commandId === null ? "" : keyHintInCatalog(backendId, tip.commandId);
  return `Tip: ${tip.lead}${shortcut}. ${tip.detail}`;
}

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }

  let pending: StartupTip | null = null;
  let splashDismissed = false;
  let shown = false;
  // A command tip waits for the catalog that supplies its button's title and shortcut.
  const show = (): void => {
    if (shown || !splashDismissed || pending === null) {
      return;
    }
    const commandId = pending.commandId;
    const command = commandId === null ? undefined : findCommandInCatalog(connection.id, commandId);
    if (commandId !== null && command === undefined) {
      return;
    }
    shown = true;
    const action: ToastAction | undefined =
      command === undefined
        ? undefined
        : {
            label: command.title,
            run: () => void runCommandFromCatalogWithFeedback(connection.id, command.id),
            passing: true,
          };
    notify("info", message(connection.id, pending), `startup-tip:${pending.id}`, action);
  };
  const offCommands = onCommandsChanged(show);
  const offSplash = onSplashDismissed(() => {
    splashDismissed = true;
    show();
  });
  const offTip = connection.host.feature("tips").on<StartupTip>("show", (tip) => {
    pending = tip;
    show();
  });
  return () => {
    offCommands();
    offSplash();
    offTip();
  };
});
