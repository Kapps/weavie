import type { InlineDiffOptions } from "../inline-diff";

export interface DiffSources {
  original: string;
  claudeVersion: string | undefined;
  acceptedBaseline: string | undefined;
}

export function sameSources(left: DiffSources, right: DiffSources): boolean {
  return (
    left.original === right.original &&
    left.claudeVersion === right.claudeVersion &&
    left.acceptedBaseline === right.acceptedBaseline
  );
}

export function hasFadedBand(options: InlineDiffOptions): boolean {
  return (
    options.mode === "applied" &&
    options.acceptedBaseline !== undefined &&
    options.acceptedBaseline !== options.original
  );
}

export function fileIsKept(options: InlineDiffOptions): boolean {
  return hasFadedBand(options) && options.original === options.claudeVersion;
}

export function reviewDiffSources(options: InlineDiffOptions): DiffSources {
  return {
    original: options.original,
    claudeVersion: options.claudeVersion,
    acceptedBaseline: hasFadedBand(options) ? options.acceptedBaseline : undefined,
  };
}
