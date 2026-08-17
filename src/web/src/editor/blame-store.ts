// The blame popover's open state. The editor owns detecting the click and knowing what the line is attributed
// to; App owns rendering the panel. This signal is the seam between them, so neither imports the other.

import { createSignal } from "solid-js";
import type { ClientSession } from "../bridge";
import type { PopoverAnchor } from "../chrome/popover-position";
import type { BlameCommit } from "./blame-model";

/** One commit the popover can show, and where the tracked line sits inside it. */
export interface BlameCommitTarget {
  commit: BlameCommit;
  /** The line's number in this commit's version of the file; 0 when the commit didn't touch the line. */
  originalLine: number;
}

/** Everything the popover needs to answer for one blamed line. */
export interface BlameTarget {
  /** The session that owns the file — every git request goes to it, never to whichever session is selected. */
  session: ClientSession;
  /** The file's absolute path, as the host addresses it. */
  path: string;
  /** The line in the current buffer, for the "other commits that changed this line" history. */
  line: number;
  /** The commit the line is attributed to — where the popover opens. */
  blamed: BlameCommitTarget;
  /** Where the annotation sits on screen, so the panel opens beside it. */
  anchor: PopoverAnchor;
}

const [target, setTarget] = createSignal<BlameTarget | null>(null);

/** The blame popover's current target, or null when it's closed. */
export const blameTarget = target;

/** Opens the popover on `next`, replacing any target already showing. */
export function openBlame(next: BlameTarget): void {
  setTarget(next);
}

/** Closes the popover. */
export function closeBlame(): void {
  setTarget(null);
}
