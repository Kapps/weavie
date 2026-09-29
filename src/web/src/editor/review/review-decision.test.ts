import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";
import { InteractionIntent } from "../../chrome/interaction-intent";
import { notify } from "../../notify/notify";
import type { TextLocation } from "../nav-history";
import { applyReviewDecision } from "./review-decision";

vi.mock("../../notify/notify", () => ({ notify: vi.fn() }));

function fixture() {
  const response = Promise.withResolvers<{ sourceDeleted: boolean; next: TextLocation | null }>();
  const request = vi.fn(() => response.promise);
  const session = { feature: () => ({ request }) } as unknown as ClientSession;
  const interaction = new InteractionIntent(new EventTarget());
  const complete = vi.fn();
  const focus = interaction.begin();
  const decision = applyReviewDecision(
    session,
    "keepHunk",
    { path: "/work/change.ts" },
    focus,
    complete,
  );
  return { response, request, interaction, complete, focus, decision };
}

describe("operation-owned review advancement", () => {
  it.each([
    { sourceDeleted: false, next: null },
    { sourceDeleted: false, next: { path: "/work/next.ts", line: 17 } },
    { sourceDeleted: true, next: { path: "/work/next.ts", line: 17 } },
  ])("advances only a completed successful decision: %j", async (result) => {
    const f = fixture();
    expect(f.request).toHaveBeenCalledExactlyOnceWith("keepHunk", { path: "/work/change.ts" });
    expect(f.complete).not.toHaveBeenCalled();
    f.response.resolve(result);
    await f.decision;
    expect(f.complete).toHaveBeenCalledTimes(result.next === null ? 0 : 1);
    if (result.next !== null)
      expect(f.complete).toHaveBeenCalledWith(result.next, f.focus, result.sourceDeleted);
    f.interaction.dispose();
  });

  it("keeps a successful mutation but drops advancement after newer interaction", async () => {
    const f = fixture();
    f.interaction.invalidate();
    f.response.resolve({ sourceDeleted: false, next: { path: "/work/next.ts", line: 1 } });
    await f.decision;
    expect(f.request).toHaveBeenCalledOnce();
    expect(f.complete).not.toHaveBeenCalled();
    f.interaction.dispose();
  });

  it("reports failure without manufacturing a navigation result", async () => {
    const f = fixture();
    f.response.reject(new Error("write failed"));
    await f.decision;
    expect(f.complete).not.toHaveBeenCalled();
    expect(notify).toHaveBeenCalledWith(
      "warn",
      "Couldn't apply review decision: Error: write failed",
    );
    f.interaction.dispose();
  });
});
