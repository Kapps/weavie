import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";

const mocks = vi.hoisted(() => ({ notify: vi.fn() }));
vi.mock("../../bridge", () => ({ selectedSession: () => null }));
vi.mock("../session-store", () => ({ activeTabFor: () => undefined }));
vi.mock("../../commands/registry", () => ({ registerCommand: () => () => {} }));
vi.mock("../../messaging/session-feature-value", () => ({
  createSessionFeatureValue: () => () => null,
}));
vi.mock("../../notify/notify", () => ({ notify: mocks.notify }));

const { acceptOption, answerPlanReview, planReviewAnswering, rejectOption } = await import(
  "./plan-review-store"
);

const review = {
  requestId: "7",
  path: "/plans/claude.md",
  editable: true,
  actions: [
    { id: "bypass", label: "Yes, and bypass permissions", kind: "allow_always" },
    { id: "default", label: "Yes, manually approve edits", kind: "allow_once" },
    { id: "reject", label: "No, keep planning", kind: "reject_once" },
  ],
};

function session(events: string[]): ClientSession {
  return {
    feature: () => ({
      publish: (name: string, payload: { optionId: string }) =>
        events.push(`${name}:${payload.optionId}`),
    }),
  } as unknown as ClientSession;
}

describe("plan review answers", () => {
  it("accepts with the first one-time allow, else the first allow", () => {
    expect(acceptOption(review)).toBe("default");
    expect(acceptOption({ ...review, actions: review.actions.slice(0, 1) })).toBe("bypass");
    expect(rejectOption(review)).toBe("reject");
  });

  it("saves an edited plan before answering it, once", async () => {
    const events: string[] = [];
    let saved!: () => void;
    const flush = vi.fn((_session: ClientSession, path: string) => {
      events.push(`flush:${path}`);
      return new Promise<void>((resolve) => {
        saved = resolve;
      });
    });
    const owner = session(events);
    const first = answerPlanReview(owner, review, "default", flush);
    await answerPlanReview(owner, review, "default", flush);
    expect(planReviewAnswering(session(events), review)).toBe(false);
    expect(planReviewAnswering(owner, review)).toBe(true);
    expect(events).toEqual(["flush:/plans/claude.md"]);

    saved();
    await first;
    expect(events).toEqual(["flush:/plans/claude.md", "permission:default"]);
    expect(planReviewAnswering(owner, review)).toBe(false);
  });

  it("does not answer when the plan cannot be saved", async () => {
    const events: string[] = [];
    await answerPlanReview(session(events), review, "default", () =>
      Promise.reject(new Error("disk full")),
    );
    expect(events).toEqual([]);
    expect(mocks.notify).toHaveBeenCalledWith("error", expect.stringContaining("disk full"));
  });

  it("answers a read-only plan without saving", async () => {
    const events: string[] = [];
    const flush = vi.fn();
    await answerPlanReview(session(events), { ...review, editable: false }, "reject", flush);
    expect(flush).not.toHaveBeenCalled();
    expect(events).toEqual(["permission:reject"]);
  });
});
