import { type Accessor, createSignal } from "solid-js";
import type { ClientSession } from "../bridge";
import { notify } from "../notify/notify";

/** A surface opened for one session; it closes, and says so, when that session ends while it is open. Opening it
 * again for the same session keeps it as it is. */
export function createOwnedSurface(title: string): {
  owner: Accessor<ClientSession | null>;
  open: (session: ClientSession) => void;
  close: () => void;
} {
  const [owner, setOwner] = createSignal<ClientSession | null>(null);
  let release = (): void => {};
  const close = (): void => {
    release();
    release = () => {};
    setOwner(null);
  };
  const ended = (): void => {
    close();
    notify("warn", `${title} closed: its session ended.`);
  };
  const open = (session: ClientSession): void => {
    if (session.signal.aborted) throw new Error(`${title} can't open: its session ended.`);
    if (owner() === session) return;
    close();
    session.signal.addEventListener("abort", ended);
    release = () => session.signal.removeEventListener("abort", ended);
    setOwner(session);
  };
  return { owner, open, close };
}
