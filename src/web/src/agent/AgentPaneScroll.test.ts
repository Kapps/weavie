import { describe, expect, it, vi } from "vitest";

vi.mock("../commands/registry", () => ({ registerCommand: () => () => {} }));

import {
  followPositionAfterRevision,
  followPositionForDistance,
  isAlignedToBottom,
  needsBottomCorrection,
} from "./AgentPaneScroll";

describe("isAlignedToBottom", () => {
  it("corrects a one-line residual gap while tolerating sub-pixel rounding", () => {
    expect(isAlignedToBottom(1_000, 375, 600)).toBe(false);
    expect(isAlignedToBottom(1_000, 399.5, 600)).toBe(true);
  });
});

describe("follow position", () => {
  it("separates exact alignment from near-bottom follow intent", () => {
    expect(followPositionForDistance(0.5, 60)).toBe("bottom");
    expect(followPositionForDistance(25, 60)).toBe("near");
    expect(followPositionForDistance(61, 60)).toBe("detached");
  });

  it("corrects passive drift only while the viewport owns the exact bottom", () => {
    expect(needsBottomCorrection("bottom", false)).toBe(true);
    expect(needsBottomCorrection("bottom", true)).toBe(false);
    expect(needsBottomCorrection("near", false)).toBe(false);
    expect(needsBottomCorrection("detached", false)).toBe(false);
  });

  it("pulls a near follower to new content without moving a detached viewport", () => {
    expect(followPositionAfterRevision("near")).toBe("bottom");
    expect(followPositionAfterRevision("bottom")).toBe("bottom");
    expect(followPositionAfterRevision("detached")).toBe("detached");
  });
});
