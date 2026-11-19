import { expect, it, vi } from "vitest";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));
vi.mock("../bridge", () => ({ registerViewFeature: () => () => {} }));

const { evaluateWhen } = await import("../commands/context");
const { onScreenContext } = await import("./on-screen");

it("defines its key as false before any session is on screen", () => {
  onScreenContext("onScreenProbe", () => true);
  expect(evaluateWhen("onScreenProbe == false")).toBe(true);
});
