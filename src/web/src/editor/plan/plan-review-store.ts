import { createEffect, createSignal, onCleanup } from "solid-js";
import { type AgentActionOption, type ClientSession, selectedSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { createSessionFeatureValue } from "../../messaging/session-feature-value";
import { activeTabFor } from "../session-store";

/** A pending agent plan approval, answered from the editor tab showing `path`. */
export interface PlanReview {
  requestId: string;
  path: string;
  editable: boolean;
  actions: AgentActionOption[];
}

export type AfterFlush = (
  session: ClientSession,
  path: string,
  action: string,
  send: () => void,
) => Promise<void>;

const reviewsFor = createSessionFeatureValue<{ reviews: PlanReview[] }, PlanReview[]>(
  "planReview",
  "reviews",
  ({ reviews }) => reviews,
);

export function planReviewsFor(session: ClientSession | null, path: string): PlanReview[] {
  return (reviewsFor(session) ?? []).filter((review) => review.path === path);
}

/** The review the plan commands answer: the active tab's newest, else the session's newest. */
function targetPlanReview(session: ClientSession | null): PlanReview | null {
  const path = session === null ? undefined : activeTabFor(session)?.entry.path;
  const reviews = reviewsFor(session) ?? [];
  return (
    (path === undefined ? undefined : planReviewsFor(session, path).at(-1)) ??
    reviews.at(-1) ??
    null
  );
}

/** Accepting picks the agent's first one-time allow, else its first allow of any kind. */
export function acceptOption(review: PlanReview): string | null {
  const allows = review.actions.filter((action) => action.kind.startsWith("allow_"));
  return (allows.find((action) => action.kind === "allow_once") ?? allows[0])?.id ?? null;
}

export function rejectOption(review: PlanReview): string | null {
  return review.actions.find((action) => action.kind.startsWith("reject_"))?.id ?? null;
}

const [answering, setAnswering] = createSignal<readonly (readonly [ClientSession, string])[]>([]);

export function planReviewAnswering(session: ClientSession, review: PlanReview): boolean {
  return answering().some(([owner, id]) => owner === session && id === review.requestId);
}

/** Saves an edited plan before answering, so the agent reads exactly what the user approved. */
export async function answerPlanReview(
  session: ClientSession,
  review: PlanReview,
  optionId: string,
  afterFlush: AfterFlush,
): Promise<void> {
  if (planReviewAnswering(session, review)) return;
  const entry = [session, review.requestId] as const;
  setAnswering((current) => [...current, entry]);
  const publish = (): void =>
    session.feature("agent").publish("permission", { requestId: review.requestId, optionId });
  try {
    await (review.editable
      ? afterFlush(session, review.path, "answering the plan", publish)
      : publish());
  } finally {
    setAnswering((current) => current.filter((candidate) => candidate !== entry));
  }
}

/** Owns the plan review commands and their context keys for the selected session. */
export function usePlanReviewCommands(afterFlush: AfterFlush): void {
  createEffect(() => {
    const session = selectedSession();
    const path = session === null ? undefined : activeTabFor(session)?.entry.path;
    setContext("planReviewPending", (reviewsFor(session) ?? []).length > 0);
    setContext("planReviewShown", path !== undefined && planReviewsFor(session, path).length > 0);
  });
  const answer =
    (choose: (review: PlanReview) => string | null) =>
    (args: unknown): boolean => {
      const session = selectedSession();
      const review = targetPlanReview(session);
      const supplied = (args as { optionId?: unknown } | undefined)?.optionId;
      const optionId =
        typeof supplied === "string" ? supplied : review === null ? null : choose(review);
      if (session === null || review === null || optionId === null) return false;
      void answerPlanReview(session, review, optionId, afterFlush);
      return true;
    };
  const offAccept = registerCommand(CommandIds.acceptAgentPlan, answer(acceptOption));
  const offReject = registerCommand(CommandIds.rejectAgentPlan, answer(rejectOption));
  onCleanup(() => {
    offAccept();
    offReject();
    setContext("planReviewPending", false);
    setContext("planReviewShown", false);
  });
}
