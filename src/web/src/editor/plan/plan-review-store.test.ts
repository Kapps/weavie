import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";

const mocks = vi.hoisted(() => ({
  commands: new Map<string, (args: unknown, context: { session: unknown }) => boolean>(),
  reviews: [] as unknown[],
}));
vi.mock("../../commands/registry", () => ({
  registerCommand: (
    id: string,
    handler: (args: unknown, context: { session: unknown }) => boolean,
  ) => {
    mocks.commands.set(id, handler);
    return () => mocks.commands.delete(id);
  },
}));
vi.mock("../../messaging/session-feature-value", () => ({
  createSessionFeatureValue: () => () => mocks.reviews,
}));

const { createRoot } = await import("solid-js");
const { evaluateWhen } = await import("../../commands/context");
const { CommandIds } = await import("../../commands/types");
const {
  acceptOption,
  answerPlanReview,
  planReviewAnswering,
  rejectOption,
  showPlanReview,
  usePlanReviewCommands,
} = await import("./plan-review-store");

const review = {
  requestId: "7",
  path: "/plans/claude.md",
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

  it("keeps planning without saving", async () => {
    const events: string[] = [];
    const afterFlush = vi.fn();
    await answerPlanReview(session(events), review, "reject", afterFlush);
    expect(afterFlush).not.toHaveBeenCalled();
    expect(events).toEqual(["permission:reject"]);
  });

  it("answers only the review whose bar owns the keys, else only a named review", async () => {
    const events: string[] = [];
    const owner = session(events);
    const aside = { ...review, requestId: "aside:3", path: "/plans/aside.md" };
    mocks.reviews = [review, aside];
    const send = (_session: ClientSession, _path: string, _action: string, publish: () => void) => {
      publish();
      return Promise.resolve();
    };
    createRoot(() => usePlanReviewCommands(send));
    const accept = mocks.commands.get(CommandIds.acceptAgentPlan)!;

    const hideAside = showPlanReview(owner, aside);
    const hidePrimary = showPlanReview(owner, review);
    hideAside();
    expect(evaluateWhen("planReviewShown")).toBe(true);
    accept(undefined, { session: owner });
    await Promise.resolve();
    expect(events).toEqual(["permission:default"]);

    hidePrimary();
    expect(evaluateWhen("planReviewShown")).toBe(false);
    expect(() => accept(undefined, { session: owner })).toThrow("No plan review is shown");
    expect(() => accept({ requestId: "9" }, { session: owner })).toThrow(
      "No pending plan review '9'",
    );
    mocks.commands.get(CommandIds.rejectAgentPlan)!({ requestId: "aside:3" }, { session: owner });
    await Promise.resolve();
    expect(events).toEqual(["permission:default", "permission:reject"]);
  });
});
