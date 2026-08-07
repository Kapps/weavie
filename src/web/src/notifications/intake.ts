import { onBackendPhase, registerHostFeature, registerSessionFeature } from "../bridge";
import { keyHintInCatalog } from "../commands/key-hint";
import { runCommandFromCatalogWithFeedback } from "../commands/registry";
import { clearNotification, notify } from "../notify/notify";
import type { Toast, ToastAction } from "../notify/Toasts";

interface NotificationAction {
  label: string;
  commandId: string;
  argsJson?: string | null;
}

interface Notification {
  level: Toast["level"];
  message: string;
  key?: string;
  action?: NotificationAction;
}

const busyKeys = new Map<string, Set<Set<string>>>();

function scopedKey(backendId: string, key: string): string {
  return `backend:${backendId}:${key}`;
}

function toastAction(backendId: string, action: NotificationAction): ToastAction {
  return {
    label: `${action.label}${keyHintInCatalog(backendId, action.commandId)}`,
    run: () => {
      try {
        const args = action.argsJson == null ? undefined : JSON.parse(action.argsJson);
        void runCommandFromCatalogWithFeedback(backendId, action.commandId, args);
      } catch (error) {
        notify("error", `Couldn't run notification action: ${String(error)}`);
      }
    },
  };
}

function install(
  backendId: string,
  feature: {
    on<T>(name: string, handler: (payload: T) => void): () => void;
  },
): () => void {
  const ownedBusy = new Set<string>();
  const backendBusy = busyKeys.get(backendId) ?? new Set<Set<string>>();
  backendBusy.add(ownedBusy);
  busyKeys.set(backendId, backendBusy);
  const offShow = feature.on<Notification>("show", ({ level, message, key, action }) => {
    const scoped = key === undefined ? undefined : scopedKey(backendId, key);
    if (scoped !== undefined) {
      if (level === "busy") {
        ownedBusy.add(scoped);
      } else {
        ownedBusy.delete(scoped);
      }
    }
    if (action === undefined) {
      notify(level, message, scoped);
    } else {
      notify(level, message, scoped, toastAction(backendId, action));
    }
  });
  const offClear = feature.on<{ key: string }>("clear", ({ key }) => {
    const scoped = scopedKey(backendId, key);
    ownedBusy.delete(scoped);
    clearNotification(scoped);
  });
  return () => {
    offShow();
    offClear();
    for (const key of ownedBusy) {
      clearNotification(key);
    }
    backendBusy.delete(ownedBusy);
    if (backendBusy.size === 0) {
      busyKeys.delete(backendId);
    }
  };
}

onBackendPhase((backendId, phase) => {
  if (phase === "online") {
    return;
  }
  for (const keys of busyKeys.get(backendId) ?? []) {
    for (const key of keys) {
      clearNotification(key);
    }
    keys.clear();
  }
});

registerHostFeature((connection) =>
  install(connection.id, connection.host.feature("notifications")),
);
registerSessionFeature((session) =>
  install(session.connection.id, session.feature("notifications")),
);
