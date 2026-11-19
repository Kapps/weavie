import { createEffect, createRoot, onCleanup } from "solid-js";
import { type ClientSession, registerViewFeature } from "../bridge";
import { setContext } from "../commands/context";

/** Runs `setup` in a reactive root that lives while `session` is the one on screen. */
export function whileOnScreen(setup: (session: ClientSession) => void): () => void {
  return registerViewFeature((session) =>
    createRoot((dispose) => {
      setup(session);
      return dispose;
    }),
  );
}

/** Keeps a `when` key equal to `read` of the session on screen, and false with none. */
export function onScreenContext(
  key: string,
  read: (session: ClientSession) => boolean,
): () => void {
  setContext(key, false);
  return whileOnScreen((session) => {
    createEffect(() => setContext(key, read(session)));
    onCleanup(() => setContext(key, false));
  });
}
