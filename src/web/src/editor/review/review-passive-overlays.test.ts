import { FloatHorizontalRange } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/view/renderingContext";
import { RangeUtil } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/viewParts/viewLines/rangeUtil";
import type { CharacterMapping } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewLayout/viewLineRenderer";
import { afterEach, describe, expect, it, vi } from "vitest";
import { measurePassiveChunks, passiveControlLeft } from "./review-passive-overlays";

vi.mock(
  "@codingame/monaco-vscode-api/vscode/vs/editor/browser/viewParts/viewLines/rangeUtil",
  () => ({
    RangeUtil: { readHorizontalRanges: vi.fn() },
  }),
);

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("passive horizontal measurements", () => {
  it("does not let removed ghost glyphs enlarge the working-model scroll range", () => {
    const staging = { style: {}, append: vi.fn(), remove: vi.fn() };
    vi.stubGlobal("document", { createElement: () => staging, body: { append: vi.fn() } });
    const modelLine = { firstElementChild: { offsetWidth: 90 } } as unknown as HTMLElement;
    const ghostLine = { firstElementChild: { offsetWidth: 2000 } } as unknown as HTMLElement;
    const text = {
      style: {},
      getBoundingClientRect: () => ({ left: 80 }),
      querySelectorAll: () => [modelLine, ghostLine],
    } as unknown as HTMLElement;
    const node = { remove: vi.fn() } as unknown as HTMLElement;
    const chunk = { node, text, modelLines: [modelLine], overlays: [] };
    const short = measurePassiveChunks([chunk], 500, 0, new Map());
    expect(short.contentWidth).toBe(500);
    expect(short.minimumContentWidth).toBe(90);
    Object.defineProperty(modelLine.firstElementChild, "offsetWidth", { value: 700 });
    const long = measurePassiveChunks([chunk], 500, 30, new Map());
    expect(long.contentWidth).toBe(730);
    expect(long.minimumContentWidth).toBe(730);
    expect(measurePassiveChunks([chunk], 500, 0, new Map()).contentWidth).toBe(700);
  });

  it("places an undecorated empty line at zero without subtracting the page offset", () => {
    const line = { dir: "", firstElementChild: {} } as HTMLElement;
    const host = { getBoundingClientRect: () => ({ left: 300 }) } as HTMLElement;
    const characters = { length: 0, getDomPosition: vi.fn() } as unknown as CharacterMapping;
    expect(passiveControlLeft(line, host, characters, host)).toBe(0);
    expect(characters.getDomPosition).not.toHaveBeenCalled();
    expect(RangeUtil.readHorizontalRanges).not.toHaveBeenCalled();
  });

  it("delegates rendered and foreign-element anchors to Monaco's normalized range reader", () => {
    const line = { dir: "", firstElementChild: {} } as HTMLElement;
    const host = {} as HTMLElement;
    const characters = {
      length: 1,
      getDomPosition: () => ({ partIndex: 1, charIndex: 0 }),
    } as unknown as CharacterMapping;
    vi.mocked(RangeUtil.readHorizontalRanges).mockReturnValue([new FloatHorizontalRange(22.5, 0)]);
    expect(passiveControlLeft(line, host, characters, host)).toBe(22.5);
    expect(RangeUtil.readHorizontalRanges).toHaveBeenCalledWith(
      line.firstElementChild,
      1,
      0,
      1,
      0,
      expect.anything(),
    );
    vi.mocked(RangeUtil.readHorizontalRanges).mockReturnValue(null);
    expect(() => passiveControlLeft(line, host, characters, host)).toThrow("no rendered position");
  });
});
