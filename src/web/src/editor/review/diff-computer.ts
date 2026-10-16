import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { IEditorWorkerService } from "@codingame/monaco-vscode-api/services";
import { monaco } from "../monaco-setup";
import { DIFF_ALGORITHM, DIFF_OPTIONS, type DiffLineChange } from "./diff-computation";

// The worker runs off the UI thread, so a long diff costs no responsiveness — and giving up showed nothing at all
// (10k changed lines of a 260k-line file take the worker ~1.7s). 0 means no time limit.
const WORKER_DIFF_OPTIONS = { ...DIFF_OPTIONS, maxComputationTimeMs: 0 };

export interface DiffSources {
  original: string;
  claudeVersion: string | undefined;
  acceptedBaseline: string | undefined;
}

export type DiffCalculation =
  | {
      status: "ready";
      changes: DiffLineChange[];
      userChanges: DiffLineChange[];
      fadedChanges: DiffLineChange[];
    }
  | { status: "failed"; error: unknown };

interface ActiveSources {
  uri: string;
  values: DiffSources;
  original: monaco.editor.ITextModel;
  claudeVersion: monaco.editor.ITextModel | undefined;
  acceptedBaseline: monaco.editor.ITextModel | undefined;
  pending: number;
  retired: boolean;
  faded: Promise<DiffLineChange[]> | undefined;
  live:
    | {
        model: monaco.editor.ITextModel;
        version: number;
        calculation: Promise<DiffCalculation>;
      }
    | undefined;
}

const completed = new WeakMap<
  monaco.editor.ITextModel,
  {
    version: number;
    sources: DiffSources;
    result: Extract<DiffCalculation, { status: "ready" }>;
  }
>();

function sameSources(left: DiffSources, right: DiffSources): boolean {
  return (
    left.original === right.original &&
    left.claudeVersion === right.claudeVersion &&
    left.acceptedBaseline === right.acceptedBaseline
  );
}

let nextComputerId = 1;

/** Computes review geometry in Monaco's existing editor worker. */
export class DiffComputer {
  private readonly id = nextComputerId++;
  private readonly worker = StandaloneServices.get(IEditorWorkerService);
  private nextSourceSetId = 1;
  private active: ActiveSources | undefined;

  public compute(
    uri: string,
    sources: DiffSources,
    liveModel: monaco.editor.ITextModel,
  ): DiffCalculation | Promise<DiffCalculation> {
    const version = liveModel.getVersionId();
    const cached = completed.get(liveModel);
    if (cached?.version === version && sameSources(cached.sources, sources)) return cached.result;
    const active = this.activate(uri, sources);
    if (active.live?.model === liveModel && active.live.version === version) {
      return active.live.calculation;
    }

    const calculation = this.track(active, this.computeActive(active, liveModel)).then((result) => {
      if (result.status === "ready" && liveModel.getVersionId() === version) {
        completed.set(liveModel, { version, sources: { ...sources }, result });
      }
      return result;
    });
    active.live = { model: liveModel, version, calculation };
    return calculation;
  }

  public clear(uri: string): void {
    if (this.active?.uri === uri) {
      this.disposeActive();
    }
  }

  public dispose(): void {
    this.disposeActive();
  }

  private activate(uri: string, sources: DiffSources): ActiveSources {
    if (this.active !== undefined && this.matches(this.active, uri, sources)) {
      return this.active;
    }
    this.disposeActive();
    const sourceSetId = this.nextSourceSetId++;
    const original = this.createSourceModel(sourceSetId, sources.original, "original");
    const claudeVersion =
      sources.claudeVersion === undefined
        ? undefined
        : this.createSourceModel(sourceSetId, sources.claudeVersion, "claude");
    const acceptedBaseline =
      sources.acceptedBaseline === undefined || sources.acceptedBaseline === sources.original
        ? undefined
        : this.createSourceModel(sourceSetId, sources.acceptedBaseline, "accepted");
    this.active = {
      uri,
      values: sources,
      original,
      claudeVersion,
      acceptedBaseline,
      pending: 0,
      retired: false,
      faded: undefined,
      live: undefined,
    };
    return this.active;
  }

  private matches(active: ActiveSources, uri: string, sources: DiffSources): boolean {
    return active.uri === uri && sameSources(active.values, sources);
  }

  private createSourceModel(
    sourceSetId: number,
    value: string,
    name: string,
  ): monaco.editor.ITextModel {
    const uri = monaco.Uri.from({
      scheme: "weavie-inline-diff",
      authority: String(this.id),
      path: `/${sourceSetId}/${name}`,
    });
    return monaco.editor.createModel(value, "plaintext", uri);
  }

  private async computeActive(
    active: ActiveSources,
    liveModel: monaco.editor.ITextModel,
  ): Promise<DiffCalculation> {
    try {
      const primary = this.computePair(active.original, liveModel);
      const user =
        active.claudeVersion === undefined
          ? Promise.resolve<DiffLineChange[]>([])
          : this.computePair(active.claudeVersion, liveModel);
      active.faded ??=
        active.acceptedBaseline === undefined
          ? Promise.resolve<DiffLineChange[]>([])
          : this.computePair(active.acceptedBaseline, active.original);
      const [changesResult, userChangesResult, fadedChangesResult] = await Promise.allSettled([
        primary,
        user,
        active.faded,
      ]);
      if (changesResult.status === "rejected") {
        return { status: "failed", error: changesResult.reason };
      }
      if (userChangesResult.status === "rejected") {
        return { status: "failed", error: userChangesResult.reason };
      }
      if (fadedChangesResult.status === "rejected") {
        return { status: "failed", error: fadedChangesResult.reason };
      }
      return {
        status: "ready",
        changes: changesResult.value,
        userChanges: userChangesResult.value,
        fadedChanges: fadedChangesResult.value,
      };
    } catch (error) {
      return { status: "failed", error };
    }
  }

  private async computePair(
    original: monaco.editor.ITextModel,
    modified: monaco.editor.ITextModel,
  ): Promise<DiffLineChange[]> {
    const result = await this.worker.computeDiff(
      original.uri,
      modified.uri,
      WORKER_DIFF_OPTIONS,
      DIFF_ALGORITHM,
    );
    if (result === null) {
      throw new Error("The editor worker could not calculate the diff");
    }
    return result.changes as DiffLineChange[];
  }

  private disposeActive(): void {
    const active = this.active;
    this.active = undefined;
    if (active === undefined) {
      return;
    }
    active.retired = true;
    if (active.pending === 0) {
      this.disposeSources(active);
    }
  }

  private track(
    active: ActiveSources,
    calculation: Promise<DiffCalculation>,
  ): Promise<DiffCalculation> {
    active.pending++;
    return calculation.finally(() => {
      active.pending--;
      if (active.retired && active.pending === 0) {
        this.disposeSources(active);
      }
    });
  }

  private disposeSources(active: ActiveSources): void {
    active.original.dispose();
    active.claudeVersion?.dispose();
    active.acceptedBaseline?.dispose();
  }
}
