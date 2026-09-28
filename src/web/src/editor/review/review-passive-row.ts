import { createTrustedTypesPolicy } from "@codingame/monaco-vscode-api/vscode/vs/base/browser/trustedTypes";
import {
  EditorFontLigatures,
  EditorOption,
  type IComputedEditorOptions,
  RenderLineNumbersType,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/config/editorOptions";
import { TextDirection } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model";
import { LineDecoration } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewLayout/lineDecorations";
import {
  RenderLineInput,
  renderViewLine2,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewLayout/viewLineRenderer";
import {
  type ViewLineData,
  ViewLineRenderingData,
  type ViewModelDecoration,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel";
import { InlineDecorationType } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel/inlineDecorations";
import type { PassiveRow } from "./review-passive-document";
import type { PassiveCharacterOverlay } from "./review-passive-overlays";

const policy = createTrustedTypesPolicy("weaviePassiveReview", { createHTML: (html) => html });

/** Synchronously consumes tokens; retained DOM/mappings do not keep mutable token data alive. */
export function renderPassiveRow(
  row: PassiveRow,
  data: ViewLineData,
  viewLine: number,
  inline: LineDecoration[],
  decorations: readonly ViewModelDecoration[],
  guideCount: number,
  height: number,
  top: number,
  options: IComputedEditorOptions,
  numbers: { cursorLine: number; viewLineCount: number; firstModelColumn: number },
  tabSize: number,
  indentSize: number,
  overlays: PassiveCharacterOverlay[],
) {
  const get = options.get.bind(options);
  const font = get(EditorOption.fontInfo);
  const layout = get(EditorOption.layoutInfo);
  let rtl = 0;
  const whole: string[] = [];
  const gutterClasses: string[] = [];
  for (const decoration of decorations) {
    const { range, options: style } = decoration;
    if (range.startLineNumber > viewLine || range.endLineNumber < viewLine) continue;
    if (style.textDirection === TextDirection.RTL) rtl++;
    if (style.textDirection === TextDirection.LTR) rtl--;
    if (style.linesDecorationsClassName) gutterClasses.push(style.linesDecorationsClassName);
    if (style.isWholeLine && style.className) whole.push(style.className);
    else if (style.className) {
      const marker = `passive-character-${overlays.length}`;
      overlays.push({
        marker,
        className: style.className,
        top,
        height,
        fill: range.endLineNumber > viewLine && style.shouldFillLineOnLineBreak === true,
      });
      inline.push(
        new LineDecoration(
          range.startLineNumber < viewLine ? data.minColumn : range.startColumn,
          range.endLineNumber > viewLine ? data.maxColumn : range.endColumn,
          marker,
          InlineDecorationType.Regular,
        ),
      );
    }
  }
  const isBasicASCII = ViewLineRenderingData.isBasicASCII(data.content, true);
  const containsRTL = ViewLineRenderingData.containsRTL(data.content, isBasicASCII, true);
  const textDirection = rtl > 0 ? TextDirection.RTL : TextDirection.LTR;
  const rendered = renderViewLine2(
    new RenderLineInput(
      font.isMonospace && !get(EditorOption.disableMonospaceOptimizations),
      font.canUseHalfwidthRightwardsArrow,
      data.content,
      data.continuesWithWrappedLine,
      isBasicASCII,
      containsRTL,
      data.minColumn - 1,
      data.tokens,
      inline,
      tabSize,
      data.startVisibleColumn,
      font.spaceWidth,
      font.middotWidth,
      font.wsmiddotWidth,
      get(EditorOption.stopRenderingLineAfter),
      get(EditorOption.renderWhitespace),
      get(EditorOption.renderControlCharacters),
      get(EditorOption.fontLigatures) !== EditorFontLigatures.OFF,
      null,
      textDirection,
      get(EditorOption.scrollbar).verticalScrollbarSize,
    ),
  );
  const line = document.createElement("div");
  line.className = "view-line";
  line.style.cssText = `top:${top}px;height:${height}px;line-height:${height}px`;
  if (textDirection === TextDirection.RTL) line.dir = "rtl";
  else if (containsRTL) line.dir = "ltr";
  line.innerHTML = (policy?.createHTML(rendered.html) ?? rendered.html) as string;
  const margin: HTMLElement[] = [];
  const background = document.createElement("div");
  background.style.cssText = `position:absolute;left:0;right:0;top:${top}px;height:${height}px;pointer-events:none`;
  if (row.removed) {
    background.className = `weavie-inline-removed${row.faded ? " weavie-inline-removed-faded" : ""}`;
    line.classList.add("weavie-inline-removed-line");
    if (row.faded) line.style.opacity = "0.7";
    for (const span of line.querySelectorAll<HTMLElement>("span")) span.style.color = "inherit";
  } else {
    background.className = whole.join(" ");
    for (let level = 0; level < guideCount; level++) {
      const guide = document.createElement("div");
      guide.className = "core-guide core-guide-indent";
      guide.style.cssText = `position:absolute;left:${level * indentSize * font.spaceWidth}px;top:0;height:${height}px;width:1px;box-shadow:1px 0 0 0 var(--vscode-editorIndentGuide-background1) inset`;
      background.append(guide);
    }
    const numbering = get(EditorOption.lineNumbers);
    if (numbers.firstModelColumn === 1 && numbering.renderType !== RenderLineNumbersType.Off) {
      const number = document.createElement("div");
      number.className = "line-numbers";
      number.style.cssText = `position:absolute;top:${top}px;left:${layout.lineNumbersLeft}px;width:${layout.lineNumbersWidth}px;height:${height}px;text-align:right;color:var(--vscode-editorLineNumber-foreground);-webkit-user-select:none;user-select:none`;
      const relative = numbering.renderType === RenderLineNumbersType.Relative;
      const interval = numbering.renderType === RenderLineNumbersType.Interval;
      number.textContent = numbering.renderFn
        ? numbering.renderFn(row.line)
        : relative && numbers.cursorLine !== row.line
          ? String(Math.abs(numbers.cursorLine - row.line))
          : interval &&
              numbers.cursorLine !== row.line &&
              row.line % 10 !== 0 &&
              row.line !== numbers.viewLineCount
            ? ""
            : String(row.line);
      if (relative && numbers.cursorLine === row.line) {
        const current = document.createElement("span");
        current.className = "relative-current-line-number";
        current.textContent = number.textContent;
        number.replaceChildren(current);
      }
      if (viewLine === numbers.viewLineCount && data.content.length === 0) {
        const final = get(EditorOption.renderFinalNewline);
        if (final === "off") number.textContent = "";
        if (final === "dimmed") number.classList.add("dimmed-line-number");
      }
      for (const decoration of decorations) {
        if (
          decoration.range.startLineNumber <= viewLine &&
          decoration.range.endLineNumber >= viewLine &&
          decoration.options.lineNumberClassName
        ) {
          number.classList.add(
            ...decoration.options.lineNumberClassName.split(" ").filter(Boolean),
          );
        }
      }
      margin.push(number);
    }
    if (gutterClasses.length) {
      const gutter = document.createElement("div");
      gutter.className = gutterClasses.join(" ");
      gutter.style.cssText = `position:absolute;left:${layout.decorationsLeft}px;top:${top}px;height:${height}px`;
      margin.push(gutter);
    }
  }
  return {
    line,
    margin,
    background,
    characters: rendered.characterMapping,
  };
}
