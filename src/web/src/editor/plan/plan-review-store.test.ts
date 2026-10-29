import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";

vi.mock("../../bridge", () => ({ selectedSession: () => null }));
vi.mock("../session-store", () => ({ activeTabFor: () => undefined }));
vi.mock("../../commands/registry", () => ({ registerCommand: () => () => {} }));
vi.mock("../../messaging/session-feature-value", () => ({
  createSessionFeatureValue: () => () => null,
}));

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
    const afterFlush = (
      _session: ClientSession,
      path: string,
      _action: string,
      send: () => void,
    ) => {
      events.push(`flush:${path}`);
      return new Promise<void>((resolve) => {
        saved = () => {
          send();
          resolve();
        };
      });
    };
    const owner = session(events);
    const first = answerPlanReview(owner, review, "default", afterFlush);
    await answerPlanReview(owner, review, "default", afterFlush);
    expect(planReviewAnswering(session(events), review)).toBe(false);
    expect(planReviewAnswering(owner, review)).toBe(true);
    expect(events).toEqual(["flush:/plans/claude.md"]);

    saved();
    await first;
    expect(events).toEqual(["flush:/plans/claude.md", "permission:default"]);
    expect(planReviewAnswering(owner, review)).toBe(false);
  });

  it("does not answer when the plan cannot be saved, and can be retried", async () => {
    const events: string[] = [];
    const owner = session(events);
    await answerPlanReview(owner, review, "default", () => Promise.resolve());
    expect(events).toEqual([]);
    expect(planReviewAnswering(owner, review)).toBe(false);
  });

  it("answers a read-only plan without saving", async () => {
    const events: string[] = [];
    const afterFlush = vi.fn();
    await answerPlanReview(session(events), { ...review, editable: false }, "reject", afterFlush);
    expect(afterFlush).not.toHaveBeenCalled();
    expect(events).toEqual(["permission:reject"]);
  });
});
