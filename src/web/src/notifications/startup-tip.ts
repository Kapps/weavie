import { registerHostFeature } from "../bridge";
import { keyHintInCatalog } from "../commands/key-hint";
import { notify } from "../notify/notify";
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
  const show = (): void => {
    if (shown || !splashDismissed || pending === null) {
      return;
    }
    shown = true;
    notify("info", message(connection.id, pending), `startup-tip:${pending.id}`);
  };
  const offSplash = onSplashDismissed(() => {
    splashDismissed = true;
    show();
  });
  const offTip = connection.host.feature("tips").on<StartupTip>("show", (tip) => {
    pending = tip;
    show();
  });
  return () => {
    offSplash();
    offTip();
  };
});
