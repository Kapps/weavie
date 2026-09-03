import type { BranchPreviewResult, EncodedImageAttachment } from "../bridge";

/** How long the prompt must sit unchanged before the composer spends a query on it. */
export const BRANCH_PREVIEW_IDLE_MS = 1200;

/**
 * Words a prompt needs before an automatic query is worth its process spawn. Attached images do not count
 * toward it — an image carries no task description on its own. A flush ignores the gate entirely.
 */
export const BRANCH_PREVIEW_MIN_WORDS = 16;

export interface BranchPreviewContext {
  backendId: string;
  prompt: string;
  attachments: readonly EncodedImageAttachment[];
}

export type BranchPreviewStatus =
  | "idle"
  | "waiting"
  | "loading"
  | "needsDetail"
  | "ready"
  | "error";

export interface BranchPreviewState {
  branch: string;
  error: string | null;
  manual: boolean;
  status: BranchPreviewStatus;
}

type PreviewRequest = (
  context: BranchPreviewContext,
  userInitiated: boolean,
  signal: AbortSignal,
) => Promise<BranchPreviewResult>;

const sameAttachments = (
  left: readonly EncodedImageAttachment[],
  right: readonly EncodedImageAttachment[],
): boolean =>
  left.length === right.length &&
  left.every((attachment, index) => {
    const other = right[index];
    return (
      other !== undefined &&
      attachment.id === other.id &&
      attachment.mime === other.mime &&
      attachment.dataB64 === other.dataB64
    );
  });

const sameSuggestion = (
  left: BranchPreviewContext | null,
  right: BranchPreviewContext | null,
): boolean =>
  left === right ||
  (left !== null &&
    right !== null &&
    left.backendId === right.backendId &&
    left.prompt === right.prompt &&
    sameAttachments(left.attachments, right.attachments));

const retargeted = (previous: BranchPreviewContext | null, next: BranchPreviewContext): boolean =>
  previous !== null && previous.backendId !== next.backendId;

const worthQuerying = (context: BranchPreviewContext): boolean =>
  context.prompt.split(/\s+/).filter((word) => word.length > 0).length >= BRANCH_PREVIEW_MIN_WORDS;

/**
 * Owns one branch suggestion per composed prompt: it queries once the prompt is worth naming and settles
 * there, so typing on never re-spends a query. Only a prompt the model calls too vague stays open, and only
 * {@link refresh} re-runs a name the user can already see.
 */
export class NewSessionBranchPreview {
  private claimed = false;
  private context: BranchPreviewContext | null = null;
  private controller: AbortController | null = null;
  private generation = 0;
  private pending: Promise<void> | null = null;
  private queried: BranchPreviewContext | null = null;
  private settled = false;
  private state: BranchPreviewState = { branch: "", error: null, manual: false, status: "idle" };
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly request: PreviewRequest,
    private readonly changed: (state: BranchPreviewState) => void,
  ) {}

  update(context: BranchPreviewContext | null): void {
    const previous = this.context;
    if (sameSuggestion(previous, context)) {
      return;
    }

    this.context = context;
    if (this.state.manual) {
      return;
    }
    if (context === null) {
      this.invalidate();
      this.settled = false;
      this.publish({ branch: "", error: null, manual: false, status: "idle" });
      return;
    }
    // A different host has different repository conventions and collisions, so it invalidates the name.
    if (retargeted(previous, context)) {
      this.settled = false;
    } else if (this.claimed || this.settled || this.controller !== null) {
      return;
    }

    this.invalidate();
    this.publish({ branch: "", error: null, manual: false, status: "waiting" });
    this.schedule();
  }

  edit(branch: string): void {
    this.invalidate();
    if (branch.trim().length > 0) {
      this.publish({ branch, error: null, manual: true, status: "ready" });
      return;
    }

    this.settled = false;
    this.publish({
      branch: "",
      error: null,
      manual: false,
      status: this.context === null ? "idle" : "waiting",
    });
    this.schedule();
  }

  /** Starts the pending query now: focus left the prompt, so no further typing is coming. */
  flush(): void {
    if (this.frozen || this.controller !== null || this.context === null) {
      return;
    }
    if (sameSuggestion(this.queried, this.context)) {
      return;
    }
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    this.run(false);
  }

  /** Settles on the name to create the session with, querying immediately when none has landed yet. */
  async resolve(): Promise<string> {
    // A query answering an older draft settles nothing: keep asking until one answers the draft being submitted.
    this.flush();
    while (this.pending !== null) {
      await this.pending;
      this.flush();
    }

    const branch = this.state.branch.trim();
    if (branch.length === 0 && !this.state.manual) {
      this.publish({
        branch: "",
        error: this.state.error ?? this.unnamed(),
        manual: false,
        status: "error",
      });
    }
    return branch;
  }

  /** Re-runs the suggestion on demand, discarding the settled name or the one the user typed. */
  refresh(): void {
    if (this.context === null) {
      return;
    }

    this.invalidate();
    this.settled = false;
    // Asking for another name is the explicit action itself, so it is not the composer's automatic work.
    this.run(true);
  }

  /** The user moved into the branch field: it is theirs to name, so nothing automatic may write over it. */
  claim(): void {
    this.claimed = true;
    this.invalidate();
  }

  /** They left the field. An empty one still needs a name, so the draft becomes nameable again. */
  release(): void {
    this.claimed = false;
    if (!this.state.manual) {
      this.schedule();
    }
  }

  cancel(): void {
    this.invalidate();
  }

  reset(): void {
    this.context = null;
    this.settled = false;
    this.invalidate();
    this.publish({ branch: "", error: null, manual: false, status: "idle" });
  }

  /** Why a submission has no name to use, at the moment it asked for one. */
  private unnamed(): string {
    return this.context === null
      ? "the host that would name it is offline."
      : "the prompt doesn't describe a specific task yet.";
  }

  private get frozen(): boolean {
    return this.claimed || this.state.manual || this.settled;
  }

  private schedule(): void {
    // Re-asking the question already answered would only spend the same query on the same prompt.
    if (this.context === null || this.frozen || sameSuggestion(this.queried, this.context)) {
      return;
    }
    if (!worthQuerying(this.context)) {
      return;
    }

    this.timer = setTimeout(() => this.run(false), BRANCH_PREVIEW_IDLE_MS);
  }

  private run(userInitiated: boolean): void {
    const context = this.context;
    if (context === null) {
      return;
    }

    this.timer = null;
    this.queried = context;
    const generation = this.generation;
    const controller = new AbortController();
    this.controller = controller;
    this.publish({ branch: "", error: null, manual: false, status: "loading" });
    this.pending = this.request(context, userInitiated, controller.signal).then(
      (result) => this.land(generation, controller, result),
      (error: unknown) =>
        this.land(generation, controller, {
          branch: "",
          error: error instanceof Error ? error.message : String(error),
          needsMoreDetail: false,
        }),
    );
  }

  private land(generation: number, controller: AbortController, result: BranchPreviewResult): void {
    if (generation !== this.generation || this.state.manual || this.controller !== controller) {
      return;
    }

    this.controller = null;
    this.pending = null;
    if (result.needsMoreDetail) {
      this.publish({ branch: "", error: null, manual: false, status: "needsDetail" });
      this.schedule();
      return;
    }

    this.settled = true;
    this.publish({
      branch: result.branch,
      error: result.error,
      manual: false,
      status: result.error === null ? "ready" : "error",
    });
  }

  private invalidate(): void {
    this.generation++;
    this.queried = null;
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    this.controller?.abort();
    this.controller = null;
    this.pending = null;
  }

  private publish(state: BranchPreviewState): void {
    this.state = state;
    this.changed(state);
  }
}
