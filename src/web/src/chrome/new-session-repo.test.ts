import { describe, expect, it } from "vitest";
import { defaultRepo, sentRepo } from "./new-session-repo";

describe("new-session repo", () => {
  it("defaults to the source session's repo, then the backend's first, then nothing", () => {
    const repos = ["/code/monorepo", "/code/infra"];
    expect(defaultRepo("/code/infra", repos)).toBe("/code/infra");
    expect(defaultRepo(undefined, repos)).toBe("/code/monorepo");
    expect(defaultRepo(undefined, [])).toBe("");
  });

  it("sends the trimmed path, the default included, and null only for a blank field", () => {
    expect(sentRepo(defaultRepo("/code/infra", []))).toBe("/code/infra");
    expect(sentRepo("  ~/code/infra ")).toBe("~/code/infra");
    expect(sentRepo("   ")).toBeNull();
    expect(sentRepo("")).toBeNull();
  });
});
