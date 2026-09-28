import type { InlineDiffActions, InlineDiffOptions, ReviewScopeState } from "../inline-diff";
import type { monaco } from "../monaco-setup";
import type { AcceptedDiffHunk, DiffHunk, HunkRevert } from "./diff-markers";
import type { ReviewPreparation } from "./review-document";
import { fileIsKept, hasFadedBand, reviewDiffSources, sameSources } from "./review-sources";

export type ReviewFileCommands = Omit<InlineDiffActions, "undoKeep" | "undoRevert" | "redoReview">;

export interface ReviewActionPresentation {
  scope: ReviewScopeState;
  valid(): boolean;
  availability(): "ready" | "pending" | "unavailable";
  reviewLine(): number;
  commentLine(): number;
  revealLine(line: number): void;
  selectLine(line: number): void;
  openComment(line: number, options: Readonly<InlineDiffOptions>): void;
  composerFocused(): boolean;
  swallowFileNavigation: boolean;
}

const run = (action: (() => void) | undefined): boolean => {
  if (action === undefined) return false;
  action();
  return true;
};

const guardCommands = (commands: ReviewFileCommands, valid: () => boolean): ReviewFileCommands =>
  Object.fromEntries(
    Object.entries(commands).map(([name, action]) => [name, () => valid() && action()]),
  ) as ReviewFileCommands;

/** Review actions belong to the exact document; paint adapters supply only their current location. */
export class ReviewFileActions {
  private disposed = false;

  public constructor(
    private readonly model: monaco.editor.ITextModel,
    private readonly preparation: () => ReviewPreparation | undefined,
    private configuration: () => Readonly<InlineDiffOptions> | undefined,
  ) {}

  private get configured(): Readonly<InlineDiffOptions> | undefined {
    return this.configuration();
  }

  public get options(): Readonly<InlineDiffOptions> | undefined {
    return this.configured;
  }

  public get geometry() {
    const result = this.preparation()?.result;
    return result?.status === "ready" ? result : undefined;
  }

  public get stale(): boolean {
    const prepared = this.preparation();
    return (
      this.disposed ||
      this.model.isDisposed() ||
      this.configured === undefined ||
      prepared === undefined ||
      prepared.version !== this.model.getVersionId() ||
      !sameSources(prepared.sources, reviewDiffSources(this.configured))
    );
  }

  public get unavailable(): boolean {
    return !this.stale && this.preparation()?.result.status !== "ready";
  }

  private get hasReview(): boolean {
    const geometry = this.geometry;
    return (
      this.configured !== undefined &&
      this.preparation() !== undefined &&
      (geometry === undefined || geometry.markers.hunks.length > 0 || hasFadedBand(this.configured))
    );
  }

  public hunkAt(line: number): DiffHunk | undefined {
    const hunks = this.geometry?.markers.hunks ?? [];
    let found = hunks[0];
    for (const hunk of hunks) {
      if (hunk.anchorLine > line) break;
      found = hunk;
    }
    return found;
  }

  private payload(hunk: DiffHunk): HunkRevert {
    return {
      baselineStart: hunk.baselineStart,
      baselineEndExclusive: hunk.baselineEndExclusive,
      currentStart: hunk.currentStart,
      currentEndExclusive: hunk.currentEndExclusive,
      guardText: this.model
        .getLinesContent()
        .slice(hunk.currentStart - 1, hunk.currentEndExclusive - 1)
        .join("\n"),
    };
  }

  public captureHunk(hunk: DiffHunk, presentation: ReviewActionPresentation) {
    const options = this.configured;
    const geometry = this.geometry;
    const valid = (): boolean =>
      options?.mode === "applied" &&
      !this.stale &&
      this.geometry === geometry &&
      this.configured === options &&
      presentation.valid() &&
      presentation.availability() === "ready" &&
      geometry?.markers.hunks.includes(hunk) === true;
    const act = (keep: boolean): boolean => {
      if (!valid()) return false;
      const callback = keep ? options!.onKeepHunk : options!.onRevertHunk;
      if (callback === undefined) return false;
      const remaining = geometry!.markers.hunks.filter((candidate) => candidate !== hunk);
      const target =
        remaining.find((candidate) => candidate.anchorLine > hunk.anchorLine) ?? remaining[0];
      callback(this.payload(hunk));
      if (valid()) {
        if (keep && target !== undefined) presentation.revealLine(target.anchorLine);
        if (remaining.length === 0 && (keep || hasFadedBand(options!))) run(options!.onNextFile);
      }
      return true;
    };
    return { keep: () => act(true), revert: () => act(false) };
  }

  public captureUnkeep(
    hunk: AcceptedDiffHunk,
    presentation: ReviewActionPresentation,
  ): () => boolean {
    const options = this.configured;
    const geometry = this.geometry;
    return () => {
      if (
        this.stale ||
        this.geometry !== geometry ||
        this.configured !== options ||
        !presentation.valid() ||
        presentation.availability() !== "ready" ||
        !geometry?.markers.acceptedHunks.includes(hunk) ||
        options?.onUnkeepHunk === undefined
      )
        return false;
      presentation.selectLine(hunk.anchorLine);
      const { anchorLine: _anchorLine, ...payload } = hunk;
      options.onUnkeepHunk(payload);
      return true;
    };
  }

  public commands(presentation: ReviewActionPresentation): ReviewFileCommands {
    const unavailable = (): boolean =>
      this.unavailable || presentation.availability() === "unavailable";
    const go = (direction: 1 | -1): boolean => {
      if (presentation.composerFocused() || this.stale || unavailable()) return false;
      const lines = this.geometry?.markers.hunks.map((hunk) => hunk.anchorLine) ?? [];
      if (lines.length === 0) return false;
      const line = presentation.reviewLine();
      const target =
        direction === 1
          ? (lines.find((candidate) => candidate > line) ?? lines[0]!)
          : (lines.findLast((candidate) => candidate < line) ?? lines.at(-1)!);
      presentation.revealLine(target);
      return true;
    };
    const file = (next: boolean): boolean =>
      !presentation.composerFocused() &&
      (run(next ? this.configured?.onNextFile : this.configured?.onPrevFile) ||
        (presentation.swallowFileNavigation && this.configured?.mode === "applied"));
    const wholeFile = (keep: boolean): boolean => {
      const options = this.configured;
      return run(
        options?.mode === "applied" && !fileIsKept(options)
          ? keep
            ? options.onKeepFile
            : options.onRevertFile
          : undefined,
      );
    };
    const all = (keep: boolean): boolean =>
      !unavailable() &&
      this.configured?.allActionsDisabled !== true &&
      run(keep ? this.configured?.onKeepAll : this.configured?.onUndo);
    const accept = (keep: boolean): boolean => {
      if (presentation.composerFocused()) return false;
      const options = this.configured;
      if (options?.mode !== "applied") return run(keep ? options?.onAccept : options?.onReject);
      if (unavailable() || presentation.scope.current === "file") return wholeFile(keep);
      if (presentation.scope.current === "all") return all(keep);
      if ((keep ? options.onKeepHunk : options.onRevertHunk) === undefined) return false;
      const hunk = this.hunkAt(presentation.reviewLine());
      if (hunk !== undefined) {
        const actions = this.captureHunk(hunk, presentation);
        if (keep) actions.keep();
        else actions.revert();
      }
      return true;
    };
    return guardCommands(
      {
        nextChange: () => go(1),
        prevChange: () => go(-1),
        nextFile: () => file(true),
        prevFile: () => file(false),
        accept: () => accept(true),
        reject: () => accept(false),
        keepFile: () => wholeFile(true),
        revertFile: () => wholeFile(false),
        keepAll: () => all(true),
        undo: () => all(false),
        comment: () => {
          if (unavailable() || this.configured?.commenting === undefined) return false;
          presentation.openComment(presentation.commentLine(), this.configured);
          return true;
        },
      },
      () =>
        !this.disposed &&
        !this.model.isDisposed() &&
        (this.hasReview || (this.configured !== undefined && unavailable())) &&
        presentation.valid() &&
        presentation.availability() !== "pending",
    );
  }

  public capture(presentation: ReviewActionPresentation): ReviewFileCommands {
    const options = this.configured;
    const geometry = this.geometry;
    const version = this.model.getVersionId();
    const line = presentation.reviewLine();
    const scope = presentation.scope.current;
    const availability = presentation.availability();
    return guardCommands(this.commands(presentation), () => {
      if (
        this.disposed ||
        this.model.isDisposed() ||
        !presentation.valid() ||
        this.model.getVersionId() !== version ||
        this.configured !== options ||
        this.geometry !== geometry ||
        presentation.availability() !== availability ||
        presentation.reviewLine() !== line ||
        presentation.scope.current !== scope
      )
        throw new Error("The review location for this command has changed.");
      return true;
    });
  }

  public dispose(): void {
    this.disposed = true;
    this.configuration = () => undefined;
  }
}
