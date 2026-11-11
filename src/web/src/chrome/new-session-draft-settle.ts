/** How long the prompt must sit unchanged before the composer spends a query on it. */
export const DRAFT_SETTLE_IDLE_MS = 1200;

/**
 * Words a prompt needs before an automatic query is worth its spend. Attached images do not count toward
 * it — an image carries no task description on its own.
 */
export const DRAFT_SETTLE_MIN_WORDS = 16;

export const draftWorthQuerying = (prompt: string): boolean =>
  prompt.split(/\s+/).filter((word) => word.length > 0).length >= DRAFT_SETTLE_MIN_WORDS;

/** Fires once a draft worth querying has sat unchanged; a shorter draft never arms it. */
export class DraftSettleTimer {
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(private readonly settled: () => void) {}

  arm(prompt: string): void {
    this.clear();
    if (draftWorthQuerying(prompt)) {
      this.timer = setTimeout(() => {
        this.timer = null;
        this.settled();
      }, DRAFT_SETTLE_IDLE_MS);
    }
  }

  clear(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
