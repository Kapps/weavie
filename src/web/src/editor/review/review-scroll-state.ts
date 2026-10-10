import type { IDisposable } from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import {
  type INewScrollPosition,
  type IScrollableOptions,
  type IScrollPosition,
  Scrollable,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";

/** Commits immediate wheel targets once per frame without exposing unpainted positions. */
export class ReviewScrollState extends Scrollable {
  private pending: IScrollPosition | null = null;
  private frame: IDisposable | null = null;
  private handlingWheel = false;
  private readonly scheduleFrame: IScrollableOptions["scheduleAtNextAnimationFrame"];

  constructor(options: IScrollableOptions) {
    super(options);
    this.scheduleFrame = options.scheduleAtNextAnimationFrame;
  }

  wheel(delegate: () => void): void {
    const previous = this.handlingWheel;
    this.handlingWheel = true;
    try {
      delegate();
    } finally {
      this.handlingWheel = previous;
    }
  }

  override getFutureScrollPosition(): IScrollPosition {
    return this.pending === null
      ? super.getFutureScrollPosition()
      : this.validateScrollPosition(this.pending);
  }

  override setScrollPositionNow(update: INewScrollPosition): void {
    if (!this.handlingWheel) {
      this.clearPending();
      super.setScrollPositionNow(update);
      return;
    }
    this.pending = this.validateScrollPosition({ ...this.getFutureScrollPosition(), ...update });
    // Cancel an old smooth animation without altering even the raw, clamped coordinates.
    super.setScrollPositionNow({});
    if (this.frame !== null) return;
    this.frame = this.scheduleFrame(() => {
      const target = this.pending!;
      this.frame = null;
      this.pending = null;
      super.setScrollPositionNow(target);
    });
  }

  override setScrollPositionSmooth(
    ...args: Parameters<Scrollable["setScrollPositionSmooth"]>
  ): void {
    this.clearPending();
    super.setScrollPositionSmooth(...args);
  }

  /** The anchor is measured before a resize; the delta also moves any uncommitted wheel target. */
  setScrollAnchor(top: number, adjustment: number): void {
    if (this.pending !== null) {
      this.pending = { ...this.pending, scrollTop: this.pending.scrollTop + adjustment };
    }
    super.setScrollPositionNow({ scrollTop: top });
  }

  private clearPending(): void {
    this.frame?.dispose();
    this.frame = null;
    this.pending = null;
  }

  override dispose(): void {
    this.clearPending();
    super.dispose();
  }
}
