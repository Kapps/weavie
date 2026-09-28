import type { ReviewCopy } from "../editor-host";
import type { InlineDiffOptions } from "../inline-diff";
import type { ReviewDocumentScope } from "./review-document";
import type { ReviewFileDiff } from "./review-store";

export interface ReviewFileConfiguration {
  diff: ReviewFileDiff;
  options: InlineDiffOptions;
}

/** The file owns registration; mounting or removing a paint adapter does not change it. */
export class ReviewFileOwner {
  private disposed = false;
  private revision = 0;
  private exists: boolean | undefined;
  private binding: { copy: ReviewCopy; input: ReviewFileConfiguration } | undefined;

  public constructor(
    private readonly documents: Pick<ReviewDocumentScope, "configure">,
    private readonly current: () => ReviewFileConfiguration | undefined,
    private readonly resolve: (diff: ReviewFileDiff) => Promise<ReviewCopy>,
  ) {}

  private clearBinding(): void {
    const binding = this.binding;
    this.binding = undefined;
    if (binding !== undefined)
      this.documents.configure(binding.copy.model.uri.toString(), undefined);
  }

  public refresh(): void {
    if (this.disposed) throw new DOMException("Review file is closed", "AbortError");
    const input = this.current();
    const exists = input?.diff.currentExists;
    if (exists !== this.exists) {
      this.exists = exists;
      this.revision++;
      this.clearBinding();
    }
    if (input !== undefined && this.binding !== undefined && this.binding.input !== input) {
      this.binding.input = input;
      this.documents.configure(this.binding.copy.model.uri.toString(), input.options);
    }
  }

  public async open(): Promise<ReviewCopy> {
    this.refresh();
    const input = this.current();
    if (input === undefined) throw new DOMException("Review file has no changes", "AbortError");
    const revision = this.revision;
    const copy = await this.resolve(input.diff);
    this.refresh();
    if (revision !== this.revision || copy.model.isDisposed())
      throw new DOMException("Review file changed while loading", "AbortError");
    if (this.binding?.copy.model !== copy.model) {
      this.clearBinding();
      this.refresh();
      if (revision !== this.revision)
        throw new DOMException("Review file changed while loading", "AbortError");
      const latest = this.current()!;
      const binding = { copy, input: latest };
      this.binding = binding;
      this.documents.configure(copy.model.uri.toString(), latest.options);
      if (this.binding !== binding || revision !== this.revision)
        throw new DOMException("Review file changed while loading", "AbortError");
    }
    return copy;
  }

  public dispose(): void {
    this.disposed = true;
    this.revision++;
    this.clearBinding();
  }
}
