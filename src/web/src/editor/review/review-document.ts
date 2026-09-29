import { toDisposable } from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import type { TextModel } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";
import type { InlineDiffOptions } from "../inline-diff";
import type { monaco } from "../monaco-setup";
import { type DiffCalculation, DiffComputer } from "./diff-computer";
import { computeDiffMarkers, type DiffMarkers } from "./diff-markers";
import { collapseUnchanged } from "./review-context";
import { ReviewFileActions } from "./review-file-actions";
import { type DiffSources, sameSources } from "./review-sources";
import { ReviewTokenization } from "./review-tokenization";

export interface ReviewGeometry {
  status: "ready";
  model: monaco.editor.ITextModel;
  version: number;
  sources: DiffSources;
  markers: DiffMarkers;
  collapsed: ReturnType<typeof collapseUnchanged>;
}

type GeometryResult = ReviewGeometry | Exclude<DiffCalculation, { status: "ready" }>;

export interface ReviewPreparation {
  version: number;
  sources: DiffSources;
  result: GeometryResult;
}

/** Review geometry belongs to an exact working model, independently of its paint adapter. */
export class ReviewDocument {
  public readonly actions: ReviewFileActions;
  private readonly computer = new DiffComputer();
  private tokenization: ReviewTokenization | undefined;
  private disposed = false;
  private sourceLeases = 0;
  private requested: { version: number; sources: DiffSources } | undefined;
  private prepared: ReviewGeometry | undefined;
  private preparation: ReviewPreparation | undefined;
  private pending:
    | { request: NonNullable<ReviewDocument["requested"]>; result: Promise<GeometryResult> }
    | undefined;

  public constructor(
    public readonly model: monaco.editor.ITextModel,
    configuration: () => Readonly<InlineDiffOptions> | undefined,
  ) {
    this.actions = new ReviewFileActions(model, () => this.preparation, configuration);
  }

  /** An active editing view keeps incremental worker inputs until its binding ends. */
  public retainSources(): monaco.IDisposable {
    if (this.disposed) throw new DOMException("Review document is closed", "AbortError");
    this.sourceLeases++;
    return toDisposable(() => {
      this.sourceLeases--;
      if (this.sourceLeases === 0) this.computer.dispose();
    });
  }

  public whenTokensAccurate(version: number, signal: AbortSignal): Promise<void> {
    signal.throwIfAborted();
    if (this.disposed || this.model.isDisposed() || this.model.getVersionId() !== version)
      throw new DOMException("Review document is closed", "AbortError");
    this.tokenization ??= new ReviewTokenization(this.model as TextModel);
    return this.tokenization.whenAccurate(version, signal);
  }

  public prepare(sources: DiffSources): GeometryResult | Promise<GeometryResult> {
    if (this.disposed || this.model.isDisposed())
      throw new DOMException("Review document is closed", "AbortError");
    const version = this.model.getVersionId();
    if (this.prepared?.version === version && sameSources(this.prepared.sources, sources)) {
      this.requested = { version, sources: this.prepared.sources };
      this.preparation = { ...this.requested, result: this.prepared };
      return this.prepared;
    }
    if (
      this.pending?.request.version === version &&
      sameSources(this.pending.request.sources, sources)
    ) {
      this.requested = this.pending.request;
      return this.pending.result;
    }
    const request = { version, sources: { ...sources } };
    this.requested = request;
    const finish = (calculation: DiffCalculation): GeometryResult => {
      if (
        this.disposed ||
        this.model.isDisposed() ||
        this.model.getVersionId() !== version ||
        this.requested !== request
      )
        throw new DOMException("Review document changed during preparation", "AbortError");
      if (calculation.status !== "ready") {
        this.preparation = { ...request, result: calculation };
        return calculation;
      }
      const markers = computeDiffMarkers(request.sources, calculation);
      this.prepared = {
        status: "ready",
        model: this.model,
        version,
        sources: request.sources,
        markers,
        collapsed: collapseUnchanged(markers, this.model.getLineCount()),
      };
      this.preparation = { ...request, result: this.prepared };
      return this.prepared;
    };
    let computation: DiffCalculation | Promise<DiffCalculation>;
    try {
      computation = this.computer.compute(this.model.uri.toString(), request.sources, this.model);
    } finally {
      // In-flight worker requests retain their source models until all pairs have settled.
      if (this.sourceLeases === 0) this.computer.dispose();
    }
    if (!(computation instanceof Promise)) return finish(computation);
    const result = computation.then(finish).finally(() => {
      if (this.pending?.request === request) this.pending = undefined;
    });
    this.pending = { request, result };
    return result;
  }

  public dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.tokenization?.dispose();
    this.tokenization = undefined;
    this.requested = undefined;
    this.prepared = undefined;
    this.preparation = undefined;
    this.actions.dispose();
    this.computer.dispose();
  }
}

/** Owns review documents for one presentation lifetime, without owning their model leases. */
export class ReviewDocumentScope {
  private readonly configurations = new Map<string, Readonly<InlineDiffOptions>>();
  private readonly configurationChanged = new Emitter<string>();
  public readonly onDidChangeConfiguration = this.configurationChanged.event;
  private readonly documents = new Map<
    monaco.editor.ITextModel,
    { document: ReviewDocument; subscription: monaco.IDisposable }
  >();
  private disposed = false;

  public forModel(model: monaco.editor.ITextModel): ReviewDocument {
    if (this.disposed || model.isDisposed())
      throw new DOMException("Review document scope is closed", "AbortError");
    const existing = this.documents.get(model);
    if (existing !== undefined) return existing.document;
    const uri = model.uri.toString();
    const document = new ReviewDocument(model, () => this.configurations.get(uri));
    const subscription = model.onWillDispose(() => {
      this.documents.delete(model);
      subscription.dispose();
      document.dispose();
    });
    this.documents.set(model, { document, subscription });
    return document;
  }

  public dispose(): void {
    this.disposed = true;
    this.configurationChanged.dispose();
    this.retain(() => false);
  }

  public get(uri: string): Readonly<InlineDiffOptions> | undefined {
    return this.configurations.get(uri);
  }

  public has(uri: string): boolean {
    return this.configurations.has(uri);
  }

  public configure(uri: string, options: InlineDiffOptions | undefined): void {
    if (this.disposed) throw new DOMException("Review document scope is closed", "AbortError");
    if (options === undefined) {
      this.retain((key) => key !== uri);
      return;
    }
    this.configurations.set(uri, Object.freeze({ ...options }));
    this.configurationChanged.fire(uri);
  }

  public retain(owns: (uri: string) => boolean): void {
    const removed = new Set<string>();
    for (const uri of this.configurations.keys()) {
      if (owns(uri)) continue;
      this.configurations.delete(uri);
      removed.add(uri);
    }
    const retired = [...this.documents].filter(([model]) => !owns(model.uri.toString()));
    for (const [model] of retired) {
      removed.add(model.uri.toString());
      this.documents.delete(model);
    }
    for (const [, { document, subscription }] of retired) {
      subscription.dispose();
      document.dispose();
    }
    for (const uri of removed) this.configurationChanged.fire(uri);
  }
}

import { Emitter } from "@codingame/monaco-vscode-api/vscode/vs/base/common/event";
