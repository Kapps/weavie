/** A path a host was handed by the OS, and the checkout it named as a fallback. */
export interface HandedOverPath {
  backendId: string;
  fallbackSlot: string | null;
}

/**
 * Which slot should show the path: the selected session's when it belongs to the backend that was handed the
 * path, else that backend's own checkout — a local file is not readable from a session running elsewhere.
 */
export function chooseOpenSlot(
  selected: { backendId: string; slot: string } | null,
  open: HandedOverPath,
): string | null {
  return selected !== null && selected.backendId === open.backendId
    ? selected.slot
    : open.fallbackSlot;
}
