import { describe, expect, it, vi } from "vitest";

// The label is pure; stubbing the feature stream keeps the DOM-dependent bridge out of a node-environment test.
vi.mock("../messaging/session-feature-value", () => ({
  createSessionFeatureValue: () => () => null,
}));

const { queuedSubmissionLabel } = await import("./agent-queue-store");

describe("queuedSubmissionLabel", () => {
  it("reads as the submitted text", () => {
    expect(queuedSubmissionLabel({ text: "/compact", attachments: 0 })).toBe("/compact");
  });

  it("counts the images of an attachment-only submission", () => {
    expect(queuedSubmissionLabel({ text: "", attachments: 1 })).toBe("1 image");
    expect(queuedSubmissionLabel({ text: "", attachments: 3 })).toBe("3 images");
  });
});
