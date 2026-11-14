import { expect, it } from "vitest";
import { rovingTarget } from "./roving-focus";

it("moves roving focus with wrapping arrows, Home and End, ignoring other keys", () => {
  expect(rovingTarget("ArrowRight", 2, 3)).toBe(0);
  expect(rovingTarget("ArrowLeft", 0, 3)).toBe(2);
  expect(rovingTarget("Home", 2, 3)).toBe(0);
  expect(rovingTarget("End", 0, 3)).toBe(2);
  expect(rovingTarget("Enter", 0, 3)).toBeNull();
  expect(rovingTarget("ArrowRight", -1, 3)).toBeNull();
});
