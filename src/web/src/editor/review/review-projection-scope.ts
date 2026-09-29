import { DOMLineBreaksComputerFactory } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/view/domLineBreaksComputer";
import type { IEditorConfiguration } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/config/editorConfiguration";
import { EditorOption } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/config/editorOptions";
import {
  type IRange,
  Range,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/core/range";
import type { TextModel } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";
import type {
  ILineBreaksComputerFactory,
  ModelLineProjectionData,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/modelLineProjectionData";
import {
  CustomLineHeightData,
  LineHeightsManager,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewLayout/lineHeights";
import { createModelLineProjection } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel/modelLineProjection";
import { MonospaceLineBreaksComputerFactory } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel/monospaceLineBreaksComputer";
import { ViewModelDecorations } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel/viewModelDecorations";
import {
  ViewModelLinesFromModelAsIs,
  ViewModelLinesFromProjectedModel,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel/viewModelLines";

/** Preparation-only Monaco mapping; consumers retain extracted shown-line data, not this scope. */
export function createReviewProjectionScope(
  model: TextModel,
  config: IEditorConfiguration,
  hidden: readonly IRange[],
) {
  const breaks = new Map<number, ModelLineProjectionData | null>();
  const capture = (factory: ILineBreaksComputerFactory): ILineBreaksComputerFactory => ({
    createLineBreaksComputer(...args) {
      const computer = factory.createLineBreaksComputer(...args);
      const requested: number[] = [];
      return {
        addRequest(line, previous) {
          requested.push(line);
          computer.addRequest(line, previous);
        },
        finalize() {
          const result = computer.finalize();
          result.forEach((data, index) => {
            breaks.set(requested[index]!, data);
          });
          return result;
        },
      };
    },
  });
  const get = config.options.get.bind(config.options);
  const unprojected = model.isTooLargeForTokenization();
  const lines = unprojected
    ? new ViewModelLinesFromModelAsIs(model)
    : new ViewModelLinesFromProjectedModel(
        0,
        model,
        capture(DOMLineBreaksComputerFactory.create(window)),
        capture(MonospaceLineBreaksComputerFactory.create(config.options)),
        get(EditorOption.fontInfo),
        model.getOptions().tabSize,
        get(EditorOption.wrappingStrategy),
        get(EditorOption.wrappingInfo).wrappingColumn,
        get(EditorOption.wrappingIndent),
        get(EditorOption.wordBreak),
        get(EditorOption.wrapOnEscapedLineFeeds),
      );
  lines.setHiddenAreas(hidden.map((range) => Range.lift(range)));
  const coordinates = lines.createCoordinatesConverter();
  const decorations = new ViewModelDecorations(0, model, config, lines, coordinates);
  const heights = new LineHeightsManager(
    get(EditorOption.lineHeight),
    get(EditorOption.allowVariableLineHeights)
      ? CustomLineHeightData.fromDecorations(
          model.getCustomLineHeightsDecorations(),
          coordinates,
          config,
        )
      : [],
  );
  return {
    lines,
    coordinates,
    decorations,
    heights,
    extractLine(modelLine: number) {
      if (!unprojected && !breaks.has(modelLine))
        throw new Error("Missing review line projection data");
      return createModelLineProjection(unprojected ? null : breaks.get(modelLine)!, true);
    },
    dispose() {
      decorations.dispose();
      lines.dispose();
      breaks.clear();
    },
  };
}

export type ReviewProjectionScope = ReturnType<typeof createReviewProjectionScope>;
