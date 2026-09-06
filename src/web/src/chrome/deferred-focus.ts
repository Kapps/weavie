export interface FocusFrames {
  request(callback: FrameRequestCallback): number;
  cancel(handle: number): void;
}

/** Owns deferred focus so an older completion cannot replace a newer interaction. */
export class DeferredFocus<Owner> {
  private generation = 0;
  private frame: number | null = null;
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
    if (this.frame !== null) {
      this.frames.cancel(this.frame);
      this.frame = null;
    }
  };

  /** Captures intent before asynchronous work; its completion may focus once. */
  capture(owner: Owner): (action: () => void) => void {
    if (this.disposed || owner !== this.owner()) return () => {};
    this.invalidate();
    const generation = this.generation;
    let completed = false;
    const current = (): boolean =>
      !this.disposed && generation === this.generation && owner === this.owner();
    return (action) => {
      if (completed || !current()) return;
      completed = true;
      action();
    };
  }

  schedule(owner: Owner, action: () => void): void {
    if (this.disposed || owner !== this.owner()) return;
    const complete = this.capture(owner);
    this.frame = this.frames.request(() =>
      complete(() => {
        this.frame = null;
        action();
      }),
    );
  }

  dispose(): void {
    this.disposed = true;
    this.invalidate();
    for (const event of this.events) {
      this.input.removeEventListener(event, this.invalidate, true);
    }
  }
}
