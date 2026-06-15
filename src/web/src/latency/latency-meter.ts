import { RollingWindow } from "./percentiles";
import type { LiveLatencyStats } from "./types";

// Live latency meter for REAL user keystrokes. Two independent, complementary signals:
//
//  1. inputToFrame  — keydown handler records the trusted event timeStamp; the next
//                     requestAnimationFrame callback subtracts it. Fine-grained
//                     (sub-ms), measures keydown -> frame-start. Excludes GPU scanout.
//  2. inputToPaint  — Event Timing API ('event' entries, durationThreshold 0): the
//                     browser's own keydown duration = input -> next paint. Authoritative
//                     but bucketed to ~8ms. `handler` = processingEnd - processingStart.
//
// Hot-path discipline (the vault's latency rule): the keydown handler does the bare
// minimum (push a timestamp, schedule one rAF). Percentile math + HUD/host updates run
// on a throttled timer, deferred well past the frame — never in the keystroke path.

const WINDOW = 240;

// PerformanceObserverInit in lib.dom does not yet include the Event Timing extension.
interface EventTimingObserverInit extends PerformanceObserverInit {
  durationThreshold?: number;
}

export class LatencyMeter {
  private readonly inputToFrame = new RollingWindow(WINDOW);
  private readonly inputToPaint = new RollingWindow(WINDOW);
  private readonly handler = new RollingWindow(WINDOW);
  private readonly frameInterval = new RollingWindow(WINDOW);

  private lastFrameTime = 0;
  private observer: PerformanceObserver | undefined;
  private loadActive = false;

  start(): void {
    // Frame cadence tracker (confirms 120Hz and flags throttling).
    const onFrame = (t: number): void => {
      if (this.lastFrameTime !== 0) {
        this.frameInterval.push(t - this.lastFrameTime);
      }
      this.lastFrameTime = t;
      requestAnimationFrame(onFrame);
    };
    requestAnimationFrame(onFrame);

    // Signal 1: fine-grained keydown -> next frame. Capture + passive: minimal hot path.
    window.addEventListener(
      "keydown",
      (event: KeyboardEvent): void => {
        const t0 = event.timeStamp;
        requestAnimationFrame((t1: number): void => {
          this.inputToFrame.push(Math.max(0, t1 - t0));
        });
      },
      { capture: true, passive: true },
    );

    // Signal 2: Event Timing API (input -> paint + handler cost).
    if (typeof PerformanceObserver !== "undefined") {
      try {
        const observer = new PerformanceObserver((list) => {
          for (const entry of list.getEntries()) {
            const event = entry as PerformanceEventTiming;
            if (event.name !== "keydown") {
              continue;
            }
            this.inputToPaint.push(event.duration);
            this.handler.push(Math.max(0, event.processingEnd - event.processingStart));
          }
        });
        // durationThreshold 0 captures every event (default is 104ms).
        const init: EventTimingObserverInit = {
          type: "event",
          durationThreshold: 0,
          buffered: true,
        };
        observer.observe(init);
        this.observer = observer;
      } catch {
        // Event Timing unsupported — inputToFrame still gives us the primary metric.
      }
    }
  }

  setLoadActive(active: boolean): void {
    this.loadActive = active;
  }

  reset(): void {
    this.inputToFrame.clear();
    this.inputToPaint.clear();
    this.handler.clear();
  }

  snapshot(): LiveLatencyStats {
    return {
      inputToFrame: this.inputToFrame.summary(),
      inputToPaint: this.inputToPaint.summary(),
      handler: this.handler.summary(),
      frameIntervalMs: this.frameInterval.summary(),
      loadActive: this.loadActive,
    };
  }

  dispose(): void {
    this.observer?.disconnect();
  }
}
