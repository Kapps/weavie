import { describe, expect, it } from "vitest";
import { focusOmnibar, focusOmnibarFileSearch, omnibarRequest } from "./omnibar-controller";

describe("focusOmnibar", () => {
  it("records the requested mode with no preload", () => {
    focusOmnibar("file");
    expect(omnibarRequest()).toMatchObject({ mode: "file", query: "", line: 1 });
    focusOmnibar("command");
    expect(omnibarRequest()?.mode).toBe("command");
  });

  it("bumps the nonce on every call so a repeat of the same mode still triggers", () => {
    focusOmnibar("file");
    const first = omnibarRequest()?.nonce;
    focusOmnibar("file");
    const second = omnibarRequest()?.nonce;
    expect(first).not.toBe(second);
  });
});

describe("focusOmnibarFileSearch", () => {
  it("carries the preload query and the link's line for a host-driven Go-to-File open", () => {
    focusOmnibarFileSearch("web/foo.ts", 42);
    expect(omnibarRequest()).toMatchObject({ mode: "file", query: "web/foo.ts", line: 42 });
  });
});
