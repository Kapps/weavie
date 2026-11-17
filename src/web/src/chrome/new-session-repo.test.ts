import { describe, expect, it } from "vitest";
import { backendRepos, defaultRepo, repoOptions, sentRepo } from "./new-session-repo";

const SESSIONS = [
  { backendId: "local", repo: "/code/monorepo" },
  { backendId: "remote", repo: "/srv/api" },
  { backendId: "local", repo: "/code/infra" },
  { backendId: "local", repo: "/code/monorepo" },
];

describe("new-session repo", () => {
  it("offers each of the chosen backend's repos once, in catalog order", () => {
    expect(backendRepos(SESSIONS, "local")).toEqual(["/code/monorepo", "/code/infra"]);
    expect(backendRepos(SESSIONS, "remote")).toEqual(["/srv/api"]);
    expect(backendRepos(SESSIONS, "gone")).toEqual([]);
  });

  it("defaults to the source session's repo, then the backend's first, then nothing", () => {
    const repos = backendRepos(SESSIONS, "local");
    expect(defaultRepo("/code/infra", repos)).toBe("/code/infra");
    expect(defaultRepo(undefined, repos)).toBe("/code/monorepo");
    expect(defaultRepo(undefined, [])).toBe("");
  });

  it("names each repo by its folder, and by its path when two share a folder name", () => {
    expect(repoOptions(["/code/monorepo", "/code/infra", "/work/infra"])).toEqual([
      { repo: "/code/monorepo", name: "monorepo" },
      { repo: "/code/infra", name: "/code/infra" },
      { repo: "/work/infra", name: "/work/infra" },
    ]);
  });

  it("sends the trimmed path, the default included, and null only for a blank field", () => {
    expect(sentRepo(defaultRepo("/code/infra", []))).toBe("/code/infra");
    expect(sentRepo("  ~/code/infra ")).toBe("~/code/infra");
    expect(sentRepo("   ")).toBeNull();
    expect(sentRepo("")).toBeNull();
  });
});
