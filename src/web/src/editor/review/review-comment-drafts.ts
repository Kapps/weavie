import { batch, createSignal } from "solid-js";
import type { CommandResult } from "../../commands/types";
import { samePath } from "../fs-path";

export interface ReviewCommentFile {
  readonly number: number;
  readonly path: string;
}

/** An opaque model incarnation is not a model lease or a DOM reference. */
export interface ReviewCommentAnchor {
  readonly incarnation: object;
  readonly version: number;
  readonly revision: string;
  readonly current: string;
  readonly line: number;
}

export interface ReviewCommentPost extends ReviewCommentFile {
  readonly line: number;
  readonly side: "right";
  readonly inReplyTo: number;
  readonly body: string;
}

export type ReviewCommentTarget =
  | { readonly kind: "new"; readonly anchor: ReviewCommentAnchor }
  | { readonly kind: "reply"; readonly rootId: number };

export interface ReviewCommentDraftState {
  readonly body: string;
  readonly revision: number;
  readonly pending: boolean;
  readonly composing: number;
  readonly error: string;
  readonly unavailable: string;
  readonly message: string;
}

export interface ReviewCommentDraft {
  readonly file: ReviewCommentFile;
  readonly target: ReviewCommentTarget;
  readonly state: () => ReviewCommentDraftState;
}

/** Session-owned text and submission state; disposing an editor never disposes these records. */
export class ReviewCommentDrafts {
  private readonly drafts = createSignal<readonly ReviewCommentDraft[]>([]);
  private readonly writers = new Map<
    ReviewCommentDraft,
    (change: (state: ReviewCommentDraftState) => ReviewCommentDraftState) => void
  >();
  private disposed = false;

  public constructor(
    private readonly post: (request: ReviewCommentPost) => Promise<CommandResult>,
    private readonly validate: (draft: ReviewCommentDraft) => string,
  ) {}

  public contains(draft: ReviewCommentDraft): boolean {
    return this.writers.has(draft);
  }

  public retained(): readonly ReviewCommentDraft[] {
    return this.drafts[0]().filter(
      (draft) =>
        draft.target.kind === "new" ||
        draft.state().body !== "" ||
        draft.state().pending ||
        draft.state().composing > 0,
    );
  }

  public open(file: ReviewCommentFile, target: ReviewCommentTarget): ReviewCommentDraft {
    if (this.disposed) throw new Error("The comment session is closed.");
    if (target.kind === "reply") {
      const existing = this.drafts[0]().find(
        (draft) =>
          draft.file.number === file.number &&
          samePath(draft.file.path, file.path) &&
          draft.target.kind === "reply" &&
          draft.target.rootId === target.rootId,
      );
      if (existing) return existing;
    }
    const [state, setState] = createSignal<ReviewCommentDraftState>({
      body: "",
      revision: 0,
      pending: false,
      composing: 0,
      error: "",
      unavailable: "",
      message: "",
    });
    const draft: ReviewCommentDraft = {
      file: Object.freeze({ ...file }),
      target: Object.freeze(
        target.kind === "new"
          ? { kind: "new", anchor: Object.freeze({ ...target.anchor }) }
          : { ...target },
      ),
      state,
    };
    setState((state) => ({ ...state, unavailable: this.validate(draft) }));
    this.writers.set(draft, setState);
    this.drafts[1]((drafts) => [...drafts, draft]);
    return draft;
  }

  public write(draft: ReviewCommentDraft, body: string): void {
    this.update(draft, (state) =>
      state.body === body
        ? state
        : { ...state, body, revision: state.revision + 1, error: "", message: "" },
    );
  }

  public refreshValidity(): void {
    for (const draft of this.drafts[0]()) {
      const unavailable = this.validate(draft);
      this.update(draft, (state) =>
        state.unavailable === unavailable ? state : { ...state, unavailable },
      );
    }
  }

  public beginComposition(draft: ReviewCommentDraft): () => void {
    this.update(draft, (state) => ({
      ...state,
      composing: state.composing + 1,
      revision: state.revision + 1,
    }));
    let released = false;
    return () => {
      if (released) return;
      released = true;
      if (this.contains(draft))
        this.update(draft, (state) => ({ ...state, composing: state.composing - 1 }));
    };
  }

  public discard(draft: ReviewCommentDraft): void {
    this.require(draft);
    if (draft.state().pending) throw new Error("This comment is still being submitted.");
    if (draft.state().composing > 0) throw new Error("This comment has an active composition.");
    this.writers.delete(draft);
    this.drafts[1]((drafts) => drafts.filter((candidate) => candidate !== draft));
  }

  public async submit(draft: ReviewCommentDraft): Promise<boolean> {
    this.require(draft);
    const submitted = draft.state();
    if (submitted.pending || submitted.composing > 0 || submitted.body.trim() === "") return false;
    const unavailable = this.validate(draft);
    if (unavailable !== "") {
      this.update(draft, (state) => ({ ...state, unavailable, error: unavailable }));
      return false;
    }
    this.update(draft, (state) => ({
      ...state,
      pending: true,
      unavailable: "",
      error: "",
      message: "",
    }));
    try {
      const result = await this.post({
        ...draft.file,
        line: draft.target.kind === "new" ? draft.target.anchor.line : 0,
        inReplyTo: draft.target.kind === "reply" ? draft.target.rootId : 0,
        side: "right",
        body: submitted.body.trim(),
      });
      if (!this.writers.has(draft)) return result.ok;
      batch(() => {
        this.update(draft, (state) => ({
          ...state,
          pending: false,
          error: result.ok ? "" : (result.error ?? result.message ?? "Couldn't post the comment."),
          message: result.ok ? "Comment posted." : "",
        }));
        if (result.ok && draft.state().revision === submitted.revision) {
          if (draft.target.kind === "new") this.discard(draft);
          else this.write(draft, "");
        }
      });
      return result.ok;
    } catch (error) {
      if (this.writers.has(draft))
        this.update(draft, (state) => ({ ...state, pending: false, error: String(error) }));
      return false;
    }
  }

  private require(draft: ReviewCommentDraft) {
    const writer = this.writers.get(draft);
    if (!writer) throw new Error("This comment draft does not belong to the live session.");
    return writer;
  }

  private update(
    draft: ReviewCommentDraft,
    change: (state: ReviewCommentDraftState) => ReviewCommentDraftState,
  ): void {
    this.require(draft)(change);
  }

  public dispose(): void {
    this.disposed = true;
    this.writers.clear();
    this.drafts[1]([]);
  }
}
