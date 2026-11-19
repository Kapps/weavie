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
  private loading = false;
  private queued: (FileIndexChange | number)[] = [];

  constructor(
    private readonly apply: (change: FileIndexChange) => void,
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

  /** A download failed or was abandoned; the list in hand, if any, is as it was. */
  abandon(): void {
    this.loading = false;
    this.queued = [];
  }

  /** A download finished at `revision`; `replaced` says whether its list was installed over the one in hand. */
  complete(revision: number, replaced: boolean): void {
    if (!replaced && revision !== this.revision) {
      throw new Error("The host kept a file list this page does not hold.");
    }
    this.loading = false;
    this.revision = revision;
    const queued = this.queued;
    this.queued = [];
    for (const step of queued) {
      if (typeof step === "number") this.replaced(step);
      else this.changed(step);
    }
  }

  changed(change: FileIndexChange): void {
    if (this.loading) {
      this.queued.push(change);
    } else if (this.revision !== null && change.revision === this.revision + 1) {
      this.apply(change);
      this.revision = change.revision;
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
}
