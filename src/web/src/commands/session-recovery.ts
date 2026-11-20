import { invokeSessionCommandOnBackend } from "../bridge";
import { confirm } from "../chrome/confirmation";
import { trackSessionCommand } from "../chrome/session-store";
import { CommandIds, type CommandResult } from "./types";

export async function invokeHostSessionCommand(
  backendId: string,
  command: string,
  args: unknown,
): Promise<CommandResult> {
  const invoke = () => invokeSessionCommandOnBackend(backendId, command, args);
  const slot = (args as { id?: unknown } | null)?.id;
  const result = await (command === CommandIds.loadSession && typeof slot === "string"
    ? trackSessionCommand(backendId, slot, invoke)
    : invoke());
  const refusal = result.data as
    | { corruptReviewSlot?: unknown; sessionName?: unknown; reason?: unknown }
    | undefined;
  if (
    result.ok ||
    typeof refusal?.corruptReviewSlot !== "string" ||
    typeof refusal.sessionName !== "string" ||
    typeof refusal.reason !== "string"
  )
    return result;

  const approved = await confirm({
    title: `Clear the saved review for “${refusal.sessionName}”?`,
    body: `${refusal.reason} Clearing the review discards its file list and decision history. Your files and conversation are kept.`,
    confirmLabel: "Clear review and load",
    destructive: true,
  });
  if (!approved) return { ok: false, cancelled: true };
  const id = refusal.corruptReviewSlot;
  const loaded = await trackSessionCommand(backendId, id, () =>
    invokeSessionCommandOnBackend(backendId, CommandIds.loadSession, {
      id,
      clearCorruptReview: true,
    }),
  );
  // Opening an existing branch selects it. The Load Session command only brings it online.
  return loaded.ok && command !== CommandIds.loadSession
    ? { ...loaded, data: { ...(loaded.data as object), activateSession: true } }
    : loaded;
}
