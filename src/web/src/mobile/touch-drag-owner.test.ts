import { expect, test } from "vitest";
import { roomAbove } from "./touch-drag-owner";

const scroller = (scrollTop: number) => ({ clientHeight: 600.5, scrollHeight: 1000, scrollTop });

test("a bottom-origin scroller has room above until it reaches its oldest content", () => {
  expect(roomAbove(scroller(0), true)).toBe(399.5);
  expect(roomAbove(scroller(-120), true)).toBe(279.5);
  expect(roomAbove(scroller(-399.5), true)).toBe(0);
});
