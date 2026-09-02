import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { IEditorWorkerService } from "@codingame/monaco-vscode-api/services";
import { monaco } from "../monaco-setup";
import { DIFF_ALGORITHM, DIFF_OPTIONS, type DiffLineChange } from "./diff-computation";

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
  | { status: "timed-out" }
  | { status: "failed"; error: unknown };

type PairCalculation = { status: "ready"; changes: DiffLineChange[] } | { status: "timed-out" };

interface ActiveSources {
  uri: string;
  values: DiffSources;
  original: monaco.editor.ITextModel;
  claudeVersion: monaco.editor.ITextModel | undefined;
  acceptedBaseline: monaco.editor.ITextModel | undefined;
  faded: Promise<PairCalculation> | undefined;
  live:
    | {
        model: monaco.editor.ITextModel;
        version: number;
        calculation: Promise<DiffCalculation>;
      }
    | undefined;
}

let nextComputerId = 1;

/** Computes review geometry in Monaco's existing editor worker. */
export class DiffComputer {
  private readonly id = nextComputerId++;
  private readonly worker = StandaloneServices.get(IEditorWorkerService);
  private active: ActiveSources | undefined;

  public compute(
    uri: string,
    sources: DiffSources,
    liveModel: monaco.editor.ITextModel,
  ): Promise<DiffCalculation> {
    const active = this.activate(uri, sources);
    const version = liveModel.getVersionId();
    if (active.live?.model === liveModel && active.live.version === version) {
      return active.live.calculation;
    }

    const calculation = this.computeActive(active, liveModel);
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
    const original = this.createSourceModel(sources.original, "original");
    const claudeVersion =
      sources.claudeVersion === undefined
        ? undefined
        : this.createSourceModel(sources.claudeVersion, "claude");
    const acceptedBaseline =
      sources.acceptedBaseline === undefined || sources.acceptedBaseline === sources.original
        ? undefined
        : this.createSourceModel(sources.acceptedBaseline, "accepted");
    this.active = {
      uri,
      values: sources,
      original,
      claudeVersion,
      acceptedBaseline,
      faded: undefined,
      live: undefined,
    };
    return this.active;
  }

  private matches(active: ActiveSources, uri: string, sources: DiffSources): boolean {
    return (
      active.uri === uri &&
      active.values.original === sources.original &&
      active.values.claudeVersion === sources.claudeVersion &&
      active.values.acceptedBaseline === sources.acceptedBaseline
    );
  }

  private createSourceModel(value: string, name: string): monaco.editor.ITextModel {
    const uri = monaco.Uri.from({
      scheme: "weavie-inline-diff",
      authority: String(this.id),
      path: `/${name}`,
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
          ? Promise.resolve<PairCalculation>({ status: "ready", changes: [] })
          : this.computePair(active.claudeVersion, liveModel);
      active.faded ??=
        active.acceptedBaseline === undefined
          ? Promise.resolve<PairCalculation>({ status: "ready", changes: [] })
          : this.computePair(active.acceptedBaseline, active.original);
      const [changes, userChanges, fadedChanges] = await Promise.all([primary, user, active.faded]);
      if (
        changes.status === "timed-out" ||
        userChanges.status === "timed-out" ||
        fadedChanges.status === "timed-out"
      ) {
        return { status: "timed-out" };
      }
      return {
        status: "ready",
        changes: changes.changes,
        userChanges: userChanges.changes,
        fadedChanges: fadedChanges.changes,
      };
    } catch (error) {
      return { status: "failed", error };
    }
  }

  private async computePair(
    original: monaco.editor.ITextModel,
    modified: monaco.editor.ITextModel,
  ): Promise<PairCalculation> {
    const result = await this.worker.computeDiff(
      original.uri,
      modified.uri,
      DIFF_OPTIONS,
      DIFF_ALGORITHM,
    );
    if (result === null) {
      throw new Error("The editor worker could not calculate the diff");
    }
    return result.quitEarly
      ? { status: "timed-out" }
      : { status: "ready", changes: result.changes as DiffLineChange[] };
  }

  private disposeActive(): void {
    this.active?.original.dispose();
    this.active?.claudeVersion?.dispose();
    this.active?.acceptedBaseline?.dispose();
    this.active = undefined;
  }
}
