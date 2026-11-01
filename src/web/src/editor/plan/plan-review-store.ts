import { createSignal, onCleanup } from "solid-js";
import type { AgentActionOption, ClientSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { type CommandContext, registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { createSessionFeatureValue } from "../../messaging/session-feature-value";

/** A pending agent plan approval, answered from the editor tab of its plan file `path`. */
export interface PlanReview {
  requestId: string;
  path: string;
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

/** Saves the plan before approving it, so the agent reads exactly what the user approved. */
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
    const allows = review.actions.some(
      (action) => action.id === optionId && action.kind.startsWith("allow_"),
    );
    await (allows ? afterFlush(session, review.path, "approving the plan", publish) : publish());
  } finally {
    setAnswering((current) => current.filter((candidate) => candidate !== entry));
  }
}

// The bar row advertising the plan keys registers its own review while its tab is active.
let shown: { session: ClientSession; review: PlanReview } | null = null;

/** Makes the plan commands answer `review` until the returned cleanup runs. */
export function showPlanReview(session: ClientSession, review: PlanReview): () => void {
  const owner = { session, review };
  shown = owner;
  setContext("planReviewShown", true);
  return () => {
    if (shown !== owner) return;
    shown = null;
    setContext("planReviewShown", false);
  };
}

/**
 * Owns the plan review commands: they answer the review whose bar registered itself, or the review named by
 * `requestId` in the invoking session.
 */
export function usePlanReviewCommands(afterFlush: AfterFlush): void {
  const target = (requestId: unknown, session: ClientSession | null) => {
    if (requestId === undefined) {
      if (shown === null) throw new Error("No plan review is shown; pass the requestId to answer.");
      return shown;
    }
    const review = (reviewsFor(session) ?? []).find(
      (candidate) => candidate.requestId === requestId,
    );
    if (session === null || review === undefined) {
      throw new Error(`No pending plan review '${String(requestId)}'.`);
    }
    return { session, review };
  };
  const answer =
    (choose: (review: PlanReview) => string | null) =>
    (args: unknown, context: CommandContext): boolean => {
      const named = args as { requestId?: unknown; optionId?: unknown } | undefined;
      const { session, review } = target(named?.requestId, context.session);
      const optionId = typeof named?.optionId === "string" ? named.optionId : choose(review);
      if (optionId === null) throw new Error("This plan review offers no such option.");
      void answerPlanReview(session, review, optionId, afterFlush);
      return true;
    };
  const offAccept = registerCommand(CommandIds.acceptAgentPlan, answer(acceptOption));
  const offReject = registerCommand(CommandIds.rejectAgentPlan, answer(rejectOption));
  onCleanup(() => {
    offAccept();
    offReject();
  });
}
