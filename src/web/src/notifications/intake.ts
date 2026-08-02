import { onBackendPhase, registerHostFeature, registerSessionFeature } from "../bridge";
import { clearNotification, notify } from "../notify/notify";
import type { Toast } from "../notify/Toasts";

interface Notification {
  level: Toast["level"];
  message: string;
  key?: string;
}

const busyKeys = new Map<string, Set<Set<string>>>();

function scopedKey(backendId: string, key: string): string {
  return `backend:${backendId}:${key}`;
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
  const offShow = feature.on<Notification>("show", ({ level, message, key }) => {
    const scoped = key === undefined ? undefined : scopedKey(backendId, key);
    if (scoped !== undefined) {
      if (level === "busy") {
        ownedBusy.add(scoped);
      } else {
        ownedBusy.delete(scoped);
      }
    }
    notify(level, message, scoped);
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
