import { describe, expect, it } from "vitest";
import { chosenRepo, OTHER_REPO } from "./new-session-repo";

const REPOS = ["/code/monorepo", "/code/infra"];

describe("the composer's chosen repo", () => {
  it("is the pick, else the source session's repo, while the list offers them", () => {
    expect(chosenRepo(["/code/infra", "/code/monorepo"], REPOS)).toBe("/code/infra");
    expect(chosenRepo([null, "/code/infra"], REPOS)).toBe("/code/infra");
    expect(chosenRepo([OTHER_REPO, "/code/infra"], REPOS)).toBe(OTHER_REPO);
  });

  it("never names a repo that left the list: it moves to one the list shows", () => {
    expect(chosenRepo(["/code/gone", "/code/infra"], REPOS)).toBe("/code/infra");
    expect(chosenRepo(["/code/gone", "/code/gone"], REPOS)).toBe("/code/monorepo");
    expect(chosenRepo([null, undefined], REPOS)).toBe("/code/monorepo");
  });

  it("asks for a path when the list offers no repo", () => {
    expect(chosenRepo([null, "/code/gone"], [])).toBe(OTHER_REPO);
  });
});
