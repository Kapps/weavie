import { invokeSessionCommandOnBackend } from "../bridge";
import { confirm } from "../chrome/confirmation";
import { CommandIds, type CommandResult } from "./types";

export async function invokeHostSessionCommand(
  backendId: string,
  command: string,
  args: unknown,
): Promise<CommandResult> {
  const result = await invokeSessionCommandOnBackend(backendId, command, args);
  const refusal = result.data as { corruptReviewSlot?: unknown; sessionName?: unknown } | undefined;
  if (
    result.ok ||
    typeof refusal?.corruptReviewSlot !== "string" ||
    typeof refusal.sessionName !== "string"
  )
    return result;

  const approved = await confirm({
    title: `Clear the saved review for “${refusal.sessionName}”?`,
    body: `${result.error} Clearing the review discards its tracked changes and decision history. Your files and conversation are kept.`,
    confirmLabel: "Clear review and load",
    destructive: true,
  });
  if (!approved) return { ok: false, cancelled: true };
  const loaded = await invokeSessionCommandOnBackend(backendId, CommandIds.loadSession, {
    id: refusal.corruptReviewSlot,
    clearCorruptReview: true,
  });
  // Opening an existing branch selects it. The Load Session command only brings it online.
  return loaded.ok && command !== CommandIds.loadSession
    ? { ...loaded, data: { ...(loaded.data as object), activateSession: true } }
    : loaded;
}
