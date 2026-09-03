export interface ScratchSaveReply {
  scratchPath: string;
  status: "saved" | "cancelled" | "failed";
  savedPath: string | null;
  error: string | null;
}

export type StableScratchSave =
  | { status: "complete"; reply: ScratchSaveReply }
  | { status: "changed"; savedPath: string | null };

/**
 * Runs a destructive scratch conversion only against one stable working-copy version. If the model changes
 * after the host copied and deleted the temp, the final flush recreates that temp before control returns.
 */
export async function saveStableScratch(
  version: () => number | undefined,
  flush: () => Promise<void>,
  save: () => Promise<ScratchSaveReply>,
): Promise<StableScratchSave> {
  const expected = version();
  if (expected === undefined) {
    return { status: "changed", savedPath: null };
  }

  await flush();
  if (version() !== expected) {
    return { status: "changed", savedPath: null };
  }

  const reply = await save();
  if (reply.status === "saved" && version() !== expected) {
    await flush();
    return { status: "changed", savedPath: reply.savedPath };
  }
  return { status: "complete", reply };
}
