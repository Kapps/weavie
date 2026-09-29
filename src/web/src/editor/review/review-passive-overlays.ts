import { DomReadingContext } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/viewParts/viewLines/domReadingContext";
import { RangeUtil } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/viewParts/viewLines/rangeUtil";
import type { CharacterMapping } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewLayout/viewLineRenderer";

export interface PassiveCharacterOverlay {
  marker: string;
  className: string;
  top: number;
  height: number;
  fill: boolean;
}

export function passiveControlLeft(
  line: HTMLElement,
  host: HTMLElement,
  characters: CharacterMapping,
  restingNode: HTMLElement,
): number {
  // Monaco's undecorated empty LTR line has no glyph rectangle; its caret starts at content offset 0.
  if (characters.length === 0 && line.dir !== "rtl") return 0;
  const position = characters.getDomPosition(characters.length);
  const ranges = RangeUtil.readHorizontalRanges(
    line.firstElementChild as HTMLElement,
    position.partIndex,
    position.charIndex,
    position.partIndex,
    position.charIndex,
    new DomReadingContext(host, restingNode),
  );
  if (!ranges?.length) throw new Error("The review hunk anchor has no rendered position");
  return ranges[0]!.left;
}

export interface PassiveChunkPaint {
  node: HTMLElement;
  text: HTMLElement;
  modelLines: HTMLElement[];
  overlays: PassiveCharacterOverlay[];
}

/** Measures the file's glyphs and character overlays together, before any scrolling. */
export function measurePassiveChunks(
  chunks: PassiveChunkPaint[],
  viewportWidth: number,
  extraSpace: number,
  anchors: ReadonlyMap<
    number,
    { line: HTMLElement; host: HTMLElement; top: number; characters: CharacterMapping }
  >,
) {
  const staging = document.createElement("div");
  staging.style.cssText = `position:fixed;left:0;top:0;width:${viewportWidth}px;height:1px;visibility:hidden;pointer-events:none;contain:strict`;
  for (const chunk of chunks) staging.append(chunk.node);
  document.body.append(staging);
  try {
    let maxLineWidth = 0;
    for (const { modelLines } of chunks) {
      for (const line of modelLines) {
        const span = line.firstElementChild as HTMLElement;
        maxLineWidth = Math.max(maxLineWidth, span.offsetWidth);
      }
    }
    const minimumContentWidth = Math.trunc(maxLineWidth + extraSpace);
    const contentWidth = Math.max(viewportWidth, minimumContentWidth);
    for (const { text } of chunks) text.style.width = `${contentWidth}px`;
    const controlPositions = new Map<number, { host: HTMLElement; top: number; left: number }>();
    for (const [line, anchor] of anchors) {
      controlPositions.set(line, {
        host: anchor.host,
        top: anchor.top,
        left: passiveControlLeft(anchor.line, anchor.host, anchor.characters, staging),
      });
    }
    const measured = chunks.map(({ text, overlays }) => {
      const left = text.getBoundingClientRect().left;
      const paints = overlays.map((overlay) => {
        const fragments = [...text.querySelectorAll<HTMLElement>(`.${overlay.marker}`)].flatMap(
          (span) =>
            [...span.getClientRects()].map((rect) => ({
              left: rect.left - left,
              right: rect.right - left,
            })),
        );
        fragments.sort((a, b) => a.left - b.left);
        const merged: { left: number; right: number }[] = [];
        for (const fragment of fragments) {
          const last = merged.at(-1);
          if (last && fragment.left <= last.right + 0.01)
            last.right = Math.max(last.right, fragment.right);
          else merged.push(fragment);
        }
        if (overlay.fill) {
          const last = merged.at(-1);
          if (last) last.right = Math.max(last.right, contentWidth);
          else merged.push({ left: 0, right: contentWidth });
        }
        return { overlay, merged };
      });
      return { text, paints };
    });
    for (const { text, paints } of measured) {
      for (const { overlay, merged } of paints) {
        for (const rect of merged) {
          const paint = document.createElement("div");
          paint.className = overlay.className;
          paint.style.cssText = `position:absolute;left:${rect.left}px;top:${overlay.top}px;width:${rect.right - rect.left}px;height:${overlay.height}px;pointer-events:none`;
          text.insertBefore(paint, text.querySelector(":scope > .view-line"));
        }
      }
    }
    return { contentWidth, minimumContentWidth, controlPositions };
  } finally {
    for (const chunk of chunks) chunk.node.remove();
    staging.remove();
  }
}
