import type { ClientSession } from "../bridge";

/**
 * Opens `path` in `session`'s editor. `line` reveals that line; `undefined` carries no target, so an
 * already-open tab stays where the user left it. The one place the reveal wire shape is written.
 */
export function revealFileIn(
  session: ClientSession | null,
  path: string,
  line: number | undefined,
  preview: boolean,
): void {
  session?.feature("files").publish("reveal", { path, line: line ?? null, preview });
}
