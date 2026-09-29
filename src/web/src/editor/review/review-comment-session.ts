import type { ReviewCommentInfo } from "../../bridge";
import type { CommandResult } from "../../commands/types";
import { samePath } from "../fs-path";
import type { monaco } from "../monaco-setup";
import {
  type ReviewCommentDraft,
  ReviewCommentDrafts,
  type ReviewCommentFile,
  type ReviewCommentPost,
} from "./review-comment-drafts";
import type { ReviewFileView } from "./review-store";

export interface ReviewCommentContext {
  readonly owner: ReviewCommentSession;
  readonly file: ReviewCommentFile;
  readonly comments: readonly ReviewCommentInfo[];
}

/** Exact-session authority for comment locations and the drafts that outlive their editors. */
export class ReviewCommentSession {
  public readonly drafts: ReviewCommentDrafts;
  private readonly models = new Map<
    monaco.editor.ITextModel,
    { incarnation: object; dispose(): void }
  >();
  private disposed = false;

  public constructor(
    private readonly files: () => readonly ReviewFileView[],
    private readonly ownsModel: (path: string, model: monaco.editor.ITextModel) => boolean,
    post: (request: ReviewCommentPost) => Promise<CommandResult>,
  ) {
    this.drafts = new ReviewCommentDrafts(post, (draft) => this.unavailable(draft));
  }

  public context(path: string): ReviewCommentContext | undefined {
    if (this.disposed) return;
    const view = this.files().find((file) => samePath(file.summary().path, path));
    const comments = view?.comments();
    if (!comments) return;
    return {
      owner: this,
      file: Object.freeze({ number: comments.number, path: view!.summary().path }),
      comments: comments.comments,
    };
  }

  private current(file: ReviewCommentFile): ReviewFileView | undefined {
    return this.files().find(
      (view) => samePath(view.summary().path, file.path) && view.comments()?.number === file.number,
    );
  }

  private unavailable(draft: ReviewCommentDraft): string {
    const view = this.current(draft.file);
    if (!view) return "This file is no longer in the same pull-request review.";
    if (draft.target.kind === "reply") {
      const rootId = draft.target.rootId;
      return view
        .comments()!
        .comments.some((comment) => comment.id === rootId && comment.inReplyTo === 0)
        ? ""
        : "This comment thread is no longer in the review.";
    }
    const diff = view.diff();
    const { anchor } = draft.target;
    const bound = [...this.models].find(
      ([, binding]) => binding.incarnation === anchor.incarnation,
    );
    return diff?.currentExists &&
      diff.revision === anchor.revision &&
      diff.current === anchor.current &&
      bound !== undefined &&
      !bound[0].isDisposed() &&
      bound[0].getVersionId() === anchor.version
      ? ""
      : "The file changed; select a new comment location.";
  }

  public refresh(): void {
    this.drafts.refreshValidity();
  }

  public openNew(
    context: ReviewCommentContext,
    model: monaco.editor.ITextModel,
    line: number,
  ): ReviewCommentDraft {
    if (this.disposed || context.owner !== this)
      throw new Error("This comment does not belong to the live session.");
    const diff = this.current(context.file)?.diff();
    if (
      !diff?.currentExists ||
      model.isDisposed() ||
      !this.ownsModel(context.file.path, model) ||
      model.getValue() !== diff.current
    )
      throw new Error("The reviewed file changed; wait for its current review before commenting.");
    if (!Number.isInteger(line) || line < 1 || line > model.getLineCount())
      throw new Error("The selected comment line is no longer in the file.");
    let binding = this.models.get(model);
    if (!binding) {
      const incarnation = {};
      const content = model.onDidChangeContent(() => this.refresh());
      const closing = model.onWillDispose(() => {
        binding!.dispose();
        this.refresh();
      });
      binding = {
        incarnation,
        dispose: () => {
          this.models.delete(model);
          content.dispose();
          closing.dispose();
        },
      };
      this.models.set(model, binding);
    }
    return this.drafts.open(context.file, {
      kind: "new",
      anchor: {
        incarnation: binding.incarnation,
        version: model.getVersionId(),
        revision: diff.revision,
        current: diff.current,
        line,
      },
    });
  }

  public openReply(context: ReviewCommentContext, rootId: number): ReviewCommentDraft {
    if (this.disposed || context.owner !== this)
      throw new Error("This comment does not belong to the live session.");
    return this.drafts.open(context.file, { kind: "reply", rootId });
  }

  public dispose(): void {
    this.disposed = true;
    for (const binding of this.models.values()) binding.dispose();
    this.drafts.dispose();
  }
}
