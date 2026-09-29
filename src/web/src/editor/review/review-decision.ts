import type { ClientSession } from "../../bridge";
import type { FocusIntent } from "../../chrome/interaction-intent";
import { notify } from "../../notify/notify";
import type { TextLocation } from "../nav-history";

export type ReviewDecision = "keepHunk" | "revertHunk" | "keepFile" | "revertFile";

export interface ReviewDecisionOutcome {
  sourceDeleted: boolean;
  sourceHasReview: boolean;
  next: TextLocation | null;
}

export type ReviewDecisionCompletion = (
  location: TextLocation,
  focus: FocusIntent,
  outcome: Pick<ReviewDecisionOutcome, "sourceDeleted" | "sourceHasReview">,
) => void;

/** A mutation response owns advancement; diff snapshots only describe the review. */
export async function applyReviewDecision<T extends { path: string }>(
  session: ClientSession,
  operation: ReviewDecision,
  payload: T,
  focus: FocusIntent | undefined,
  complete: ReviewDecisionCompletion,
): Promise<void> {
  try {
    const result = await session
      .feature("review")
      .request<ReviewDecisionOutcome, T>(operation, payload);
    if (result.next !== null && focus?.current()) complete(result.next, focus, result);
  } catch (error) {
    notify("warn", `Couldn't apply review decision: ${String(error)}`);
  }
}
