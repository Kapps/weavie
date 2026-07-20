import { describe, expect, it } from "vitest";
import { focusOmnibar, omnibarRequest } from "./omnibar-controller";

describe("focusOmnibar", () => {
  it("records the requested mode", () => {
    focusOmnibar("file", "");
    expect(omnibarRequest()?.mode).toBe("file");
    focusOmnibar("command", "");
    expect(omnibarRequest()?.mode).toBe("command");
  });

  it("carries the preload query for a host-driven Go-to-File open", () => {
    focusOmnibar("file", "web/foo.ts");
    expect(omnibarRequest()?.query).toBe("web/foo.ts");
  });

  it("bumps the nonce on every call so a repeat of the same mode still triggers", () => {
    focusOmnibar("file", "");
    const first = omnibarRequest()?.nonce;
    focusOmnibar("file", "");
    const second = omnibarRequest()?.nonce;
    expect(first).not.toBe(second);
  });
});
