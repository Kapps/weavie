const ESC = "\u001b";
const BEL = "\u0007";

/**
 * Whether an xterm onData delivery is a synthesized answer to a device query (CPR `ESC[…R`, DA/DA2/DA3 `…c`,
 * DSR `…n`, DECRPM `…$y`, or an OSC/DCS reply). During a scrollback-replay parse these must not reach the
 * child — they were answered in the pane's previous life — but nothing a user can type takes these forms
 * (arrow keys end in A–D, F-keys use `ESC O`/`…~`, Alt chords are `ESC` + a printable), so everything else
 * is real input and must pass through.
 */
export function isReplayedQueryAnswer(data: string): boolean {
  if (!data.startsWith(ESC) || data.length < 3) {
    return false;
  }
  const kind = data[1];
  const last = data[data.length - 1];
  if (kind === "[") {
    if (last === "R" || last === "c" || last === "n" || last === "y") {
      return true;
    }
    // The kitty-keyboard flags reply (CSI ? flags u); real kitty key events never carry the '?' prefix.
    return last === "u" && data[2] === "?";
  }
  if (kind === "]") {
    return data.endsWith(BEL) || data.endsWith(`${ESC}\\`);
  }
  return kind === "P" && data.endsWith(`${ESC}\\`);
}
