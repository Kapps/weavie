import type { TextModel } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";
import type { DiffMarkers } from "./diff-markers";
import type { ReviewDocument, ReviewGeometry } from "./review-document";
import { reviewDiffSources } from "./review-sources";
import type { ReviewFileDiff } from "./review-store";

export interface PassiveRow {
  line: number;
  text: string;
  removed: boolean;
  faded: boolean;
}

export interface PassiveDocument {
  model: TextModel;
  version: number;
  language: string;
  markers: DiffMarkers;
  collapsed: ReviewGeometry["collapsed"];
  rows: PassiveRow[];
}

/** Prepares the exact working model without constructing an editor widget. */
export async function preparePassiveDocument(
  document: ReviewDocument,
  diff: ReviewFileDiff,
  signal: AbortSignal,
): Promise<PassiveDocument> {
  const { model } = document;
  const sources = reviewDiffSources({
    mode: "applied",
    original: diff.baseline,
    claudeVersion: diff.current,
    acceptedBaseline: diff.acceptedBaseline,
  });
  const calculation = await document.prepare(sources);
  signal.throwIfAborted();
  if (
    model.isDisposed() ||
    (calculation.status === "ready" && model.getVersionId() !== calculation.version)
  ) {
    throw new DOMException("Passive document changed during preparation", "AbortError");
  }
  if (calculation.status === "failed") throw calculation.error;
  if (calculation.status === "timed-out") throw new Error("Diff calculation timed out");
  const { markers, collapsed, version } = calculation;
  const internal = model as TextModel;
  const language = model.getLanguageId();
  await document.whenTokensAccurate(version, signal);
  signal.throwIfAborted();
  if (model.isDisposed() || model.getVersionId() !== version || model.getLanguageId() !== language)
    throw new DOMException("Passive document changed during tokenization", "AbortError");
  const ghosts = new Map<number, PassiveRow[]>();
  for (const ghost of markers.ghosts) {
    const rows = ghosts.get(ghost.afterLineNumber) ?? [];
    for (const text of ghost.lines) {
      rows.push({
        line: ghost.afterLineNumber,
        text,
        removed: true,
        faded: ghost.faded,
      });
    }
    ghosts.set(ghost.afterLineNumber, rows);
  }
  const rows: PassiveRow[] = [...(ghosts.get(0) ?? [])];
  let hidden = 0;
  for (let line = 1; line <= model.getLineCount(); line++) {
    while (collapsed.hidden[hidden] !== undefined && collapsed.hidden[hidden]!.endLineNumber < line)
      hidden++;
    const range = internal.isTooLargeForTokenization() ? undefined : collapsed.hidden[hidden];
    if (range === undefined || line < range.startLineNumber) {
      rows.push({
        line,
        text: model.getLineContent(line),
        removed: false,
        faded: false,
      });
    }
    for (const ghost of ghosts.get(line) ?? []) rows.push(ghost);
  }
  return { model: internal, version, language, markers, collapsed, rows };
}
