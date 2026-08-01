import { registerHostFeature, registerSessionFeature } from "../bridge";
import { clearNotification, notify } from "../notify/notify";
import type { Toast } from "../notify/Toasts";

interface Notification {
  level: Toast["level"];
  message: string;
  key?: string;
}

function install(feature: {
  on<T>(name: string, handler: (payload: T) => void): () => void;
}): () => void {
  const offShow = feature.on<Notification>("show", ({ level, message, key }) =>
    notify(level, message, key),
  );
  const offClear = feature.on<{ key: string }>("clear", ({ key }) => clearNotification(key));
  return () => {
    offShow();
    offClear();
  };
}

registerHostFeature((connection) => install(connection.host.feature("notifications")));
registerSessionFeature((session) => install(session.feature("notifications")));
