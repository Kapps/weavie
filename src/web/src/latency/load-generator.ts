// Simulates main-thread contention to measure under-load tail latency — standing in
// for the terminal output firehose weavie will have once xterm.js is wired (step 2).
// Each animation frame it burns a tunable slice of CPU on the UI thread, competing
// with Monaco for the frame budget. This is the condition where web UIs show their
// real weakness (tail latency), so it must be part of a rigorous harness.

export class LoadGenerator {
  private running = false;
  private rafHandle = 0;

  /** @param busyMsPerFrame synthetic main-thread work per frame (ms). */
  constructor(private readonly busyMsPerFrame = 5) {}

  get active(): boolean {
    return this.running;
  }

  start(): void {
    if (this.running) {
      return;
    }
    this.running = true;
    const tick = (): void => {
      if (!this.running) {
        return;
      }
      this.burn(this.busyMsPerFrame);
      this.rafHandle = requestAnimationFrame(tick);
    };
    this.rafHandle = requestAnimationFrame(tick);
  }

  stop(): void {
    this.running = false;
    if (this.rafHandle !== 0) {
      cancelAnimationFrame(this.rafHandle);
      this.rafHandle = 0;
    }
  }

  // Busy-wait that the optimizer cannot elide: accumulates into a sink read by the DOM.
  private sink = 0;
  private burn(ms: number): void {
    const deadline = performance.now() + ms;
    let acc = this.sink;
    while (performance.now() < deadline) {
      acc += Math.sqrt(acc + 1) * 1.0000001;
    }
    this.sink = acc % 1_000_000;
  }
}
