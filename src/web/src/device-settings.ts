// Device settings: what the screen in front of the user reads (fonts, theme, editor options, notification prefs,
// agent display). The page's home host owns them; no other host's values reach these.

import { hostInjected, registerHomeFeature } from "./bridge";

/** One device setting group, live. */
export interface DeviceSetting<T> {
  /** The value to use right now. */
  current(): T;
  /** Subscribes to changes; returns an unsubscribe function. */
  onChanged(handler: (value: T) => void): () => void;
}

/**
 * The group the home host injects as `window[global]` before navigation, then sends as `name` in its hello and
 * in a `settings` push on each change.
 */
export function deviceSetting<T>(
  name: string,
  global: string,
  injected: T | undefined,
  devFallback: T,
): DeviceSetting<T> {
  let current = hostInjected(global, injected, devFallback);
  const subscribers = new Set<(value: T) => void>();
  const apply = (value: T): void => {
    current = value;
    for (const handler of subscribers) {
      handler(value);
    }
  };
  registerHomeFeature((connection) => {
    // A hello repeats what the page holds unless a change was pushed while it was disconnected.
    const offHello = connection.onInitializing((hello) => {
      const value = hello.deviceSettings[name] as T;
      if (JSON.stringify(value) !== JSON.stringify(current)) {
        apply(value);
      }
    });
    const offPush = connection.host.feature("settings").on<T>(name, apply);
    return () => {
      offHello();
      offPush();
    };
  });
  return {
    current: () => current,
    onChanged: (handler) => {
      subscribers.add(handler);
      return () => {
        subscribers.delete(handler);
      };
    },
  };
}
