import type { EditorBinding, SpellProjection } from "../../bridge";

/** A spelling reply may affect the visible editor only when every immutable projection field still agrees. */
export function ownsSpellProjection(
  binding: EditorBinding | null,
  message: SpellProjection,
): boolean {
  return (
    binding !== null &&
    binding.protocol === "projection" &&
    binding.sessionId === message.sessionId &&
    binding.projectionEpoch === message.projectionEpoch &&
    binding.projectionRevision === message.projectionRevision &&
    binding.projectionPageId === message.projectionPageId
  );
}

/** Extracts the direct word accepted by dictionary commands invoked outside an editor menu. */
export function directSpellWord(args: unknown): string | null {
  if (typeof args !== "object" || args === null) {
    return null;
  }
  const word = (args as Record<string, unknown>).word;
  return typeof word === "string" && word.length > 0 ? word : null;
}
