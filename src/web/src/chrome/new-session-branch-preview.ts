import type { BranchPreviewResult } from "../bridge";

export const BRANCH_PREVIEW_DEBOUNCE_MS = 500;

export interface BranchPreviewContext {
  backendId: string;
  prompt: string;
  providerId: "claude" | "codex";
}

export type BranchPreviewStatus = "idle" | "waiting" | "loading" | "ready" | "error";

export interface BranchPreviewState {
  branch: string;
  manual: boolean;
  status: BranchPreviewStatus;
}

type PreviewRequest = (
  context: BranchPreviewContext,
  signal: AbortSignal,
) => Promise<BranchPreviewResult>;

const sameContext = (
  left: BranchPreviewContext | null,
  right: BranchPreviewContext | null,
): boolean =>
  left === right ||
  (left !== null &&
    right !== null &&
    left.backendId === right.backendId &&
    left.prompt === right.prompt &&
    left.providerId === right.providerId);

/** Owns one debounced, cancellable branch preview without allowing stale or automatic writes over user input. */
export class NewSessionBranchPreview {
  private context: BranchPreviewContext | null = null;
  private controller: AbortController | null = null;
  private generation = 0;
  private state: BranchPreviewState = { branch: "", manual: false, status: "idle" };
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly request: PreviewRequest,
    private readonly changed: (state: BranchPreviewState) => void,
  ) {}

  update(context: BranchPreviewContext | null): void {
    if (sameContext(this.context, context)) {
      return;
    }

    this.context = context;
    this.invalidate();
    if (this.state.manual) {
      return;
    }

    this.publish({ branch: "", manual: false, status: context === null ? "idle" : "waiting" });
    this.schedule();
  }

  edit(branch: string): void {
    this.invalidate();
    if (branch.trim().length > 0) {
      this.publish({ branch, manual: true, status: "ready" });
      return;
    }

    this.publish({
      branch: "",
      manual: false,
      status: this.context === null ? "idle" : "waiting",
    });
    this.schedule();
  }

  cancel(): void {
    this.invalidate();
  }

  reset(): void {
    this.context = null;
    this.invalidate();
    this.publish({ branch: "", manual: false, status: "idle" });
  }

  private schedule(): void {
    const context = this.context;
    if (context === null || this.state.manual) {
      return;
    }

    const generation = this.generation;
    this.timer = setTimeout(() => {
      this.timer = null;
      const controller = new AbortController();
      this.controller = controller;
      this.publish({ branch: "", manual: false, status: "loading" });
      void this.request(context, controller.signal).then(
        (result) => {
          if (!this.isCurrent(generation, context, controller)) {
            return;
          }
          this.controller = null;
          this.publish({ branch: result.branch, manual: false, status: "ready" });
        },
        () => {
          if (!this.isCurrent(generation, context, controller)) {
            return;
          }
          this.controller = null;
          this.publish({ branch: "", manual: false, status: "error" });
        },
      );
    }, BRANCH_PREVIEW_DEBOUNCE_MS);
  }

  private isCurrent(
    generation: number,
    context: BranchPreviewContext,
    controller: AbortController,
  ): boolean {
    return (
      generation === this.generation &&
      !this.state.manual &&
      this.controller === controller &&
      sameContext(this.context, context)
    );
  }

  private invalidate(): void {
    this.generation++;
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    this.controller?.abort();
    this.controller = null;
  }

  private publish(state: BranchPreviewState): void {
    this.state = state;
    this.changed(state);
  }
}
