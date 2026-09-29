import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { ILanguageService } from "@codingame/monaco-vscode-api/services";
import { applyFontInfo } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/config/domFontInfo";
import { EditorConfiguration } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/config/editorConfiguration";
import { EditorOption } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/config/editorOptions";
import { Position } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/core/position";
import { Range } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/core/range";
import { PositionAffinity } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model";
import { LineTokens } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/tokens/lineTokens";
import { LineDecoration } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewLayout/lineDecorations";
import { ViewLineData } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel";
import { IAccessibilityService } from "@codingame/monaco-vscode-api/vscode/vs/platform/accessibility/common/accessibility.service";
import { MenuId } from "@codingame/monaco-vscode-api/vscode/vs/platform/actions/common/actions";
import { reviewHunkLine } from "../diff-geometry";
import type { monaco } from "../monaco-setup";
import { buildNewFileBadge, NEW_FILE_BADGE_HEIGHT } from "./diff-zones";
import type { ReviewCommentGeometry } from "./review-comment-layout";
import { reviewEditorConfiguration } from "./review-editor-configuration";
import { createReviewLineGeometry } from "./review-line-geometry";
import { PassiveCopyMap } from "./review-passive-copy";
import type { PassiveDocument } from "./review-passive-document";
import {
  measurePassiveChunks,
  type PassiveCharacterOverlay,
  type PassiveChunkPaint,
} from "./review-passive-overlays";
import { renderPassiveRow } from "./review-passive-row";
import { createReviewProjectionModel } from "./review-projection-model";
import { createReviewProjectionScope, type ReviewProjectionScope } from "./review-projection-scope";

export interface PassiveChunk {
  node: HTMLElement;
  top: number;
  height: number;
}

/** Finite rendered-view chunks; all projection/layout work finishes before scrolling. */
export function renderPassiveChunks(
  documentModel: PassiveDocument,
  width: number,
  editorOptions: monaco.editor.IEditorOptions,
  cursorLine: number,
  comments: readonly ReviewCommentGeometry[],
) {
  const { model, rows, markers, collapsed } = documentModel;
  const chunkSize = 4;
  const configuration = reviewEditorConfiguration(model, editorOptions);
  const config = new EditorConfiguration(
    false,
    MenuId.EditorContext,
    true,
    {
      ...configuration,
      dimension: { width, height: 0 },
    },
    null,
    StandaloneServices.get(IAccessibilityService),
  );
  let scope: ReviewProjectionScope | undefined;
  try {
    config.setIsDominatedByLongLines(model.isDominatedByLongLines());
    config.setModelLineCount(model.getLineCount());
    const get = config.options.get.bind(config.options);
    scope = createReviewProjectionScope(
      createReviewProjectionModel(model, 0, [...markers.decorations, ...collapsed.gapMarkers]),
      config,
      collapsed.hidden,
    );
    const font = get(EditorOption.fontInfo);
    const layout = get(EditorOption.layoutInfo);
    const lineHeight = get(EditorOption.lineHeight);
    const { tabSize, indentSize } = model.getOptions();
    const codec = StandaloneServices.get(ILanguageService).languageIdCodec;
    const copy = new PassiveCopyMap(rows);
    const commentTops = new Map<ReviewCommentGeometry["zone"], number>();
    const orderedComments = comments
      .map((item) => ({
        ...item,
        viewLine: scope!.coordinates.convertModelPositionToViewPosition(
          new Position(item.line, model.getLineMaxColumn(item.line)),
          undefined,
          true,
        ).lineNumber,
      }))
      .sort((a, b) => a.viewLine - b.viewLine || a.ordinal - b.ordinal);
    let nextComment = 0;
    const chunks: PassiveChunk[] = [];
    const paints: PassiveChunkPaint[] = [];
    const viewTops: number[] = [];
    const controlLines = new Set(
      [...markers.hunks, ...markers.acceptedHunks].map((hunk) =>
        reviewHunkLine(hunk.anchorLine, model.getLineCount()),
      ),
    );
    const controlAnchors = new Map<
      number,
      {
        line: HTMLElement;
        host: HTMLElement;
        top: number;
        characters: ReturnType<typeof renderPassiveRow>["characters"];
      }
    >();
    const shownLines: number[] = [];
    let top = get(EditorOption.padding).top;
    if (markers.isNewFile) {
      const badge = buildNewFileBadge();
      badge.style.height = `${NEW_FILE_BADGE_HEIGHT}px`;
      chunks.push({ node: badge, top, height: NEW_FILE_BADGE_HEIGHT });
      top += NEW_FILE_BADGE_HEIGHT;
    }
    let node!: HTMLDivElement;
    let text!: HTMLDivElement;
    let chunkHeight = 0;
    let chunkRows = 0;
    let renderedRows = 0;
    let overlays: PassiveCharacterOverlay[] = [];
    let modelLines: HTMLElement[] = [];
    const finishChunk = (): void => {
      if (!chunkRows) return;
      node.style.height = text.style.height = `${chunkHeight}px`;
      const lane = document.createElement("div");
      lane.style.cssText = `position:absolute;left:${layout.contentLeft}px;right:0;top:0;height:${chunkHeight}px;overflow:hidden`;
      lane.append(text);
      node.append(lane);
      paints.push({ node, text, modelLines, overlays });
      chunks.push({ node, top, height: chunkHeight });
      top += chunkHeight;
      chunkRows = chunkHeight = 0;
      overlays = [];
      modelLines = [];
    };
    const placeCommentsBefore = (viewLine: number): void => {
      while (
        nextComment < orderedComments.length &&
        orderedComments[nextComment]!.viewLine < viewLine
      ) {
        finishChunk();
        const item = orderedComments[nextComment++]!;
        commentTops.set(item.zone, top);
        top += item.height;
      }
    };
    for (const [sourceIndex, row] of rows.entries()) {
      if (!row.removed) shownLines.push(row.line);
      const first = row.removed
        ? 1
        : scope.coordinates.convertModelPositionToViewPosition(
            new Position(row.line, 1),
            PositionAffinity.Left,
          ).lineNumber;
      const projection = row.removed ? null : scope.extractLine(row.line);
      if (!row.removed) placeCommentsBefore(first);
      const count = projection?.getViewLineCount() ?? 1;
      const end = first + count - 1;
      const data = row.removed
        ? [
            new ViewLineData(
              row.text,
              false,
              1,
              row.text.length + 1,
              0,
              LineTokens.createEmpty(row.text, codec),
              null,
            ),
          ]
        : scope.lines.getViewLinesData(first, end, new Array(count).fill(true));
      const decorations = row.removed
        ? undefined
        : scope.decorations.getDecorationsViewportData(
            new Range(first, data[0]!.minColumn, end, data.at(-1)!.maxColumn),
          );
      const guides =
        !row.removed && get(EditorOption.guides).indentation
          ? scope.lines.getViewLinesIndentGuides(first, end)
          : [];
      for (let wrap = 0; wrap < count; wrap++) {
        if (chunkRows === chunkSize) finishChunk();
        if (chunkRows === 0) {
          node = document.createElement("div");
          node.className = "monaco-editor monaco-editor-background passive-review-chunk";
          node.style.cssText = `position:absolute;width:${width}px;overflow:hidden`;
          node.style.willChange = "transform";
          applyFontInfo(node, font);
          text = document.createElement("div");
          text.className = "view-lines";
          text.style.cssText = `position:absolute;left:0;top:0;width:${layout.contentWidth}px;white-space:nowrap`;
        }
        const viewLine = first + wrap;
        const lineData = data[wrap]!;
        const height = row.removed ? lineHeight : scope.heights.heightForLineNumber(viewLine);
        if (!row.removed) viewTops[viewLine - 1] = top + chunkHeight;
        const painted = renderPassiveRow(
          row,
          lineData,
          viewLine,
          LineDecoration.filter(
            [
              ...(decorations?.inlineDecorations[wrap] ?? []),
              ...(lineData.inlineDecorations ?? []),
            ],
            viewLine,
            lineData.minColumn,
            lineData.maxColumn,
          ),
          decorations?.decorations ?? [],
          guides[wrap] ?? 0,
          height,
          chunkHeight,
          config.options,
          {
            cursorLine,
            viewLineCount: scope.lines.getViewLineCount(),
            firstModelColumn:
              projection?.getModelColumnOfViewPosition(wrap, lineData.minColumn) ?? 1,
          },
          tabSize,
          indentSize,
          overlays,
        );
        text.prepend(painted.background);
        text.append(painted.line);
        if (!row.removed) modelLines.push(painted.line);
        if (!row.removed && controlLines.has(row.line))
          controlAnchors.set(row.line, {
            line: painted.line,
            host: text,
            top: chunkHeight,
            characters: painted.characters,
          });
        for (const margin of painted.margin) node.append(margin);
        copy.register(painted.line, {
          row: sourceIndex,
          wrap,
          projection,
          characters: painted.characters,
        });
        chunkHeight += height;
        chunkRows++;
        renderedRows++;
      }
    }
    finishChunk();
    placeCommentsBefore(Number.POSITIVE_INFINITY);
    const viewportWrapping = get(EditorOption.wrappingInfo).isViewportWrapping;
    const { contentWidth, minimumContentWidth, controlPositions } = measurePassiveChunks(
      paints,
      layout.contentWidth,
      viewportWrapping
        ? 0
        : get(EditorOption.scrollBeyondLastColumn) * font.typicalHalfwidthCharacterWidth +
            layout.verticalScrollbarWidth,
      controlAnchors,
    );
    const coordinates = scope.coordinates;
    const geometry = createReviewLineGeometry(model.getLineCount(), shownLines, (line) => {
      const viewLine = coordinates.convertModelPositionToViewPosition(new Position(line, 1));
      const position = viewTops[viewLine.lineNumber - 1];
      if (position === undefined) throw new Error("Missing passive review line position");
      return position;
    });
    return {
      configuration,
      commentTops,
      chunks,
      copy,
      layout,
      contentWidth,
      minimumContentWidth,
      controlPositions,
      viewportWrapping,
      textLayers: paints.map((paint) => paint.text),
      geometry,
      renderedRows,
      // Monaco publishes integer content dimensions, but keeps fractional line positions.
      height: Math.trunc(top + get(EditorOption.padding).bottom),
      lineHeight,
    };
  } finally {
    scope?.dispose();
    config.dispose();
  }
}
