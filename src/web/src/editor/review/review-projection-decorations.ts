import { deepClone, equals } from "@codingame/monaco-vscode-api/vscode/vs/base/common/objects";
import type { IModelDecoration } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model";
import {
  ModelDecorationOptions,
  type TextModel,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";

export function reviewOwnsDecoration(ownerId: number, decoration: { ownerId: number }): boolean {
  return decoration.ownerId === 0 || decoration.ownerId === ownerId;
}

interface DecorationSnapshot {
  id: string;
  range: readonly number[];
  options: IModelDecoration["options"];
}

const presentationOptions = (options: ModelDecorationOptions) => {
  const { description: _description, stickiness: _stickiness, ...presentation } = options;
  return presentation;
};
const emptyPresentation = presentationOptions(ModelDecorationOptions.EMPTY);
const renderedOptions = new WeakMap<ModelDecorationOptions, boolean>();

function hasPresentation(options: IModelDecoration["options"]): boolean {
  if (!(options instanceof ModelDecorationOptions)) return true;
  let rendered = renderedOptions.get(options);
  if (rendered === undefined) {
    // Compare against Monaco's empty options, ignoring only debug labels and edit-tracking affinity.
    rendered = !equals(presentationOptions(options), emptyPresentation);
    renderedOptions.set(options, rendered);
  }
  return rendered;
}

/** Includes synthesized bracket/font decorations through the last column of the working model. */
export function captureReviewDecorations(model: TextModel): readonly DecorationSnapshot[] {
  return model
    .getDecorationsInRange(model.getFullModelRange())
    .filter((value) => reviewOwnsDecoration(0, value) && hasPresentation(value.options))
    .map(({ id, range, options }) => ({
      id,
      range: [range.startLineNumber, range.startColumn, range.endLineNumber, range.endColumn],
      // Stored options are immutable; providers can synthesize fresh plain options on every query.
      options: options instanceof ModelDecorationOptions ? options : deepClone(options),
    }));
}

export function sameReviewDecorations(
  before: readonly DecorationSnapshot[],
  after: readonly DecorationSnapshot[],
): boolean {
  return (
    before.length === after.length &&
    before.every((entry, index) => {
      const other = after[index]!;
      return (
        entry.id === other.id &&
        equals(entry.range, other.range) &&
        (entry.options instanceof ModelDecorationOptions
          ? entry.options === other.options
          : equals(entry.options, other.options))
      );
    })
  );
}
