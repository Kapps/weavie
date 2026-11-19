/** One step of a session's file list: the paths that take revision `revision - 1` to `revision`. */
export interface FileIndexChange {
  revision: number;
  added: readonly string[];
  removed: readonly string[];
}

/**
 * Reconciles a downloaded file list with the changes published while it was in flight. A change is applied
 * only as the exact next revision; anything else means the list in hand can't be stepped, so it is downloaded
 * again.
 */
export class FileIndexSync {
  private revision: number | null = null;
  // Changes wait from the start: nothing can be stepped until the first download says which list is in hand.
  private loading = true;
  private queued: (FileIndexChange | number)[] = [];

  /** `apply` reports whether the change fit the list in hand; one that didn't is answered by a download. */
  constructor(
    private readonly apply: (change: FileIndexChange) => boolean,
    private readonly download: () => void,
  ) {}

  /** The revision of the list in hand, or null before the first download completes. */
  get known(): number | null {
    return this.revision;
  }

  /** A download started; changes wait for it rather than land on the list it replaces. */
  begin(): void {
    this.loading = true;
  }

  /**
   * A download failed or was abandoned. The list in hand takes the changes that waited; with no list there is
   * nothing to step, so they are dropped and the next change or a retry downloads afresh.
   */
  abandon(): void {
    if (this.revision === null) this.queued = [];
    this.settle();
  }

  /** A download finished at `revision`; `replaced` says whether its list was installed over the one in hand. */
  complete(revision: number, replaced: boolean): void {
    if (!replaced && revision !== this.revision) {
      throw new Error("The host kept a file list this page does not hold.");
    }
    this.revision = revision;
    this.settle();
  }

  changed(change: FileIndexChange): void {
    if (this.loading) {
      this.queued.push(change);
    } else if (this.revision !== null && change.revision === this.revision + 1) {
      if (this.apply(change)) this.revision = change.revision;
      else this.download();
    } else if (this.revision === null || change.revision > this.revision) {
      this.download();
    }
  }

  /** The host changed more paths at `revision` than it publishes one by one. */
  replaced(revision: number): void {
    if (this.loading) {
      this.queued.push(revision);
    } else if (this.revision === null || revision > this.revision) {
      this.download();
    }
  }

  private settle(): void {
    this.loading = false;
    const queued = this.queued;
    this.queued = [];
    for (const step of queued) {
      if (typeof step === "number") this.replaced(step);
      else this.changed(step);
    }
  }
}
