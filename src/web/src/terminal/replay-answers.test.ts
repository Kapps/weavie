import { describe, expect, it } from "vitest";
import { isReplayedQueryAnswer } from "./replay-answers";

const ESC = "\u001b";
const BEL = "\u0007";

describe("isReplayedQueryAnswer", () => {
  it("matches the answers xterm synthesizes to replayed queries", () => {
    expect(isReplayedQueryAnswer(`${ESC}[19;23R`)).toBe(true); // CPR
    expect(isReplayedQueryAnswer(`${ESC}[?1;2c`)).toBe(true); // DA
    expect(isReplayedQueryAnswer(`${ESC}[>0;276;0c`)).toBe(true); // DA2
    expect(isReplayedQueryAnswer(`${ESC}[0n`)).toBe(true); // DSR
    expect(isReplayedQueryAnswer(`${ESC}[?2026;1$y`)).toBe(true); // DECRPM
    expect(isReplayedQueryAnswer(`${ESC}]10;rgb:ff/ff/ff${BEL}`)).toBe(true); // OSC color reply
    expect(isReplayedQueryAnswer(`${ESC}P1$r0m${ESC}\\`)).toBe(true); // DCS reply
    expect(isReplayedQueryAnswer(`${ESC}[?1u`)).toBe(true); // kitty-keyboard flags reply
  });

  it("passes real user input through, including escape-prefixed keys", () => {
    expect(isReplayedQueryAnswer("ls -la\r")).toBe(false); // typed text + Enter
    expect(isReplayedQueryAnswer("R")).toBe(false); // a plain letter an answer ends with
    expect(isReplayedQueryAnswer(`${ESC}[A`)).toBe(false); // arrow up
    expect(isReplayedQueryAnswer(`${ESC}[1;5D`)).toBe(false); // ctrl+arrow
    expect(isReplayedQueryAnswer(`${ESC}OR`)).toBe(false); // F3
    expect(isReplayedQueryAnswer(`${ESC}[15~`)).toBe(false); // F5
    expect(isReplayedQueryAnswer(`${ESC}r`)).toBe(false); // alt+r
    expect(isReplayedQueryAnswer(`${ESC}[13;2u`)).toBe(false); // kitty-encoded Shift+Enter (no '?' prefix)
    expect(isReplayedQueryAnswer(ESC)).toBe(false); // bare escape key
  });
});
