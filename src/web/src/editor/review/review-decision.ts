import type { ClientSession } from "../../bridge";
import type { FocusIntent } from "../../chrome/interaction-intent";
import { notify } from "../../notify/notify";
import type { TextLocation } from "../nav-history";

export type ReviewDecisionCompletion = (
  location: TextLocation,
  focus: FocusIntent,
  sourceDeleted: boolean,
) => void;

/** A mutation response owns advancement; diff snapshots only describe the review. */
export async function applyReviewDecision<T extends { path: string }>(
  session: ClientSession,
  operation: "keepHunk" | "revertHunk" | "keepFile" | "revertFile",
  payload: T,
  focus: FocusIntent | undefined,
  complete: ReviewDecisionCompletion,
): Promise<void> {
  try {
    const result = await session
      .feature("review")
      .request<{ sourceDeleted: boolean; next: TextLocation | null }, T>(operation, payload);
    if (result.next !== null && focus?.current())
      complete(result.next, focus, result.sourceDeleted);
  } catch (error) {
    notify("warn", `Couldn't apply review decision: ${String(error)}`);
  }
}
