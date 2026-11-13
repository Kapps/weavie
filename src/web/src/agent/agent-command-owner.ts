import { insideOmnibar } from "../chrome/omnibar-controller";
import { setContext } from "../commands/context";
import { registerCommand } from "../commands/registry";

const owners = new Map<string, () => void>();
const targets = new Map<string, unknown>();

/**
 * Gives `element` command `commandId` until the user moves on: a press anywhere but the element and the omnibar
 * (where the command is run from) releases it, and so does the next owner. Focus moves don't, since the pane moves
 * focus itself (to the composer after an attachment). A press inside the element releases it too when
 * `pressReleases`, for owners whose state that press clears (a text highlight).
 */
export function ownCommand(
  commandId: string,
  context: string,
  element: Element,
  run: () => boolean | undefined,
  pressReleases: boolean,
): () => void {
  return own(commandId, context, element, pressReleases, registerCommand(commandId, run));
}

/**
 * Makes `target` what context `context`'s commands act on, under {@link ownCommand}'s release rules; the commands
 * keep one handler that reads it with {@link ownedTarget}.
 */
export function ownTarget<T>(context: string, element: Element, target: T): () => void {
  const release = own(context, context, element, false, () => targets.delete(context));
  targets.set(context, target);
  return release;
}

/** The target that owns context `context`, if one does. */
export function ownedTarget<T>(context: string): T | undefined {
  return targets.get(context) as T | undefined;
}

/** Whether `release` is the current owner's release for `key`, a command id or a target's context. */
export function ownsCommand(key: string, release: (() => void) | null): boolean {
  return release !== null && owners.get(key) === release;
}

function own(
  key: string,
  context: string,
  element: Element,
  pressReleases: boolean,
  off: () => void,
): () => void {
  owners.get(key)?.();
  setContext(context, true);
  const onPress = (event: Event): void => {
    const target = event.target as Node | null;
    if (insideOmnibar(target) || (!pressReleases && element.contains(target))) return;
    release();
  };
  const release = (): void => {
    if (owners.get(key) !== release) return;
    owners.delete(key);
    document.removeEventListener("pointerdown", onPress, true);
    off();
    setContext(context, false);
  };
  document.addEventListener("pointerdown", onPress, true);
  owners.set(key, release);
  return release;
}
