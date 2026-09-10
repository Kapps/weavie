export interface FocusIntent {
  complete(action: () => void): void;
  dispose(): void;
}

export interface FocusFrames {
  request(callback: FrameRequestCallback): number;
  cancel(handle: number): void;
}

/** Owns deferred focus so an older completion cannot replace a newer interaction. */
export class DeferredFocus<Owner> {
  private generation = 0;
  private frame: number | null = null;
  private pending = false;
  private recovery: { owner: Owner; generation: number; action: () => void } | null = null;
  private disposed = false;
  private readonly events = ["pointerdown", "keydown", "focusin"];

  constructor(
    private readonly owner: () => Owner,
    private readonly frames: FocusFrames,
    private readonly input: EventTarget,
  ) {
    for (const event of this.events) {
      input.addEventListener(event, this.invalidate, true);
    }
  }

  readonly invalidate = (): void => {
    this.generation += 1;
    this.pending = false;
    this.recovery = null;
    if (this.frame !== null) {
      this.frames.cancel(this.frame);
      this.frame = null;
    }
  };

  /** Captures explicit intent; completion or disposal releases its focus ownership. */
  capture(owner: Owner): FocusIntent {
    if (this.disposed || owner !== this.owner()) return { complete: () => {}, dispose: () => {} };
    this.invalidate();
    this.pending = true;
    const generation = this.generation;
    const current = (): boolean =>
      !this.disposed && generation === this.generation && owner === this.owner() && this.pending;
    return {
      complete: (action) => {
        if (!current()) return;
        this.pending = false;
        this.recovery = null;
        action();
      },
      dispose: () => {
        if (!current()) return;
        this.pending = false;
        this.scheduleRecovery();
      },
    };
  }

  /** Passive blur recovery yields to explicit intent and subsequent input. */
  recover(owner: Owner, action: () => void): void {
    if (this.disposed || owner !== this.owner()) return;
    this.recovery = { owner, generation: this.generation, action };
    this.scheduleRecovery();
  }

  private scheduleRecovery(): void {
    if (this.pending || this.recovery === null) return;
    if (this.frame !== null) this.frames.cancel(this.frame);
    const recovery = this.recovery;
    this.frame = this.frames.request(() => {
      if (
        this.disposed ||
        this.recovery !== recovery ||
        recovery.generation !== this.generation ||
        recovery.owner !== this.owner() ||
        this.pending
      )
        return;
      this.frame = null;
      this.recovery = null;
      recovery.action();
    });
  }

  dispose(): void {
    this.disposed = true;
    this.invalidate();
    for (const event of this.events) {
      this.input.removeEventListener(event, this.invalidate, true);
    }
  }
}
