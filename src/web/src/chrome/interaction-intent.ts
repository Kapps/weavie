export interface FocusIntent {
  current(): boolean;
}

/** User input retires delayed focus, but a command's own programmatic focus return does not. */
export class InteractionIntent {
  private generation = 0;
  private disposed = false;
  private readonly events = ["pointerdown", "keydown", "wheel"];

  constructor(private readonly input: EventTarget) {
    for (const event of this.events)
      input.addEventListener(event, this.invalidate, { capture: true, passive: true });
  }

  readonly invalidate = (): void => {
    this.generation++;
  };

  readonly capture = (): FocusIntent => {
    const generation = this.generation;
    return { current: () => !this.disposed && generation === this.generation };
  };

  readonly begin = (): FocusIntent => {
    this.invalidate();
    return this.capture();
  };

  dispose(): void {
    this.disposed = true;
    for (const event of this.events) this.input.removeEventListener(event, this.invalidate, true);
  }
}
