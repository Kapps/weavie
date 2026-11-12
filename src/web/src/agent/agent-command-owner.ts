import { insideOmnibar } from "../chrome/omnibar-controller";
import { setContext } from "../commands/context";
import { registerCommand } from "../commands/registry";

const owners = new Map<string, () => void>();

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
  owners.get(commandId)?.();
  const off = registerCommand(commandId, run);
  setContext(context, true);
  const onPress = (event: Event): void => {
    const target = event.target as Node | null;
    if (insideOmnibar(target) || (!pressReleases && element.contains(target))) return;
    release();
  };
  const release = (): void => {
    if (owners.get(commandId) !== release) return;
    owners.delete(commandId);
    document.removeEventListener("pointerdown", onPress, true);
    off();
    setContext(context, false);
  };
  document.addEventListener("pointerdown", onPress, true);
  owners.set(commandId, release);
  return release;
}

/** Whether `release` is the current owner's release for `commandId`. */
export function ownsCommand(commandId: string, release: (() => void) | null): boolean {
  return release !== null && owners.get(commandId) === release;
}
