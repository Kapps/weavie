import { afterEach, describe, expect, it, vi } from "vitest";
import type { monaco } from "../monaco-setup";
import { createReviewCommentLayout } from "./review-comment-layout";
import type { ReviewCommentPlacement } from "./review-comment-view";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));
const captured = vi.hoisted(() => ({ placement: undefined as ReviewCommentPlacement | undefined }));
vi.mock("./review-comment-view", () => ({
  createReviewCommentPresenter: (placement: ReviewCommentPlacement) => {
    captured.placement = placement;
    return { dispose: vi.fn() };
  },
}));

function element() {
  return { style: { cssText: "", left: "", top: "" }, append: vi.fn(), remove: vi.fn() };
}

function fixture() {
  vi.stubGlobal("document", { createElement: element });
  const host = element();
  const layout = createReviewCommentLayout(host as unknown as HTMLElement, () => null);
  const node = element();
  const zone = {
    afterLineNumber: 3,
    heightInPx: 40,
    ordinal: 1,
    domNode: node as unknown as HTMLElement,
  };
  const placed = captured.placement!.place(zone);
  const zones = new Map<string, monaco.editor.IViewZone>();
  let id = 0;
  let left = 60;
  let onLayout = () => {};
  const removeZone = vi.fn((key: string) => {
    zones.get(key)?.onDomNodeTop?.(-1_000_100);
    zones.delete(key);
  });
  const editor = {
    getScrollTop: () => 100,
    getLayoutInfo: () => ({ contentLeft: left }),
    onDidLayoutChange: (handler: () => void) => {
      onLayout = handler;
      return { dispose: vi.fn() };
    },
    changeViewZones: (action: (accessor: unknown) => void) =>
      action({
        addZone: (value: monaco.editor.IViewZone) => {
          const key = String(++id);
          zones.set(key, value);
          value.onDomNodeTop?.(20);
          return key;
        },
        removeZone,
        layoutZone: vi.fn(),
      }),
  } as unknown as monaco.editor.IStandaloneCodeEditor;
  return {
    layout,
    host,
    zone,
    node,
    placed,
    zones,
    editor,
    removeZone,
    resize: (value: number) => {
      left = value;
      onLayout();
    },
    bind: () => layout.bind(editor, (change) => change()),
  };
}

afterEach(() => vi.unstubAllGlobals());

describe("review comment placement ownership", () => {
  it("reserves live space synchronously and restores passive positions after an offscreen sentinel", () => {
    const f = fixture();
    f.layout.place(new Map([[f.zone, 240]]), 50);
    const live = f.bind();
    expect(f.zones.size).toBe(1);
    expect(f.node.style.top).toBe("120px");
    expect(f.host.style.left).toBe("60px");
    const spacer = [...f.zones.values()][0]!;
    spacer.onDomNodeTop!(-1_000_100);
    expect(f.node.style.top).toBe("-1000000px");
    live.dispose();
    expect(f.node.style.top).toBe("240px");
    expect(f.host.style.left).toBe("50px");
    spacer.onDomNodeTop!(900);
    f.resize(90);
    expect(f.node.style.top).toBe("240px");
    expect(f.host.style.left).toBe("50px");
    live.dispose();
    expect(f.removeZone).toHaveBeenCalledTimes(1);
    f.placed.dispose();
  });

  it("defers prepared passive placement until the live owner releases it", () => {
    const f = fixture();
    const live = f.bind();
    f.layout.place(new Map([[f.zone, 300]]), 70);
    expect(f.node.style.top).toBe("120px");
    expect(f.host.style.left).toBe("60px");
    f.resize(80);
    expect(f.host.style.left).toBe("80px");
    expect(() => f.bind()).toThrow("already have a live presentation");
    live.dispose();
    expect(f.node.style.top).toBe("300px");
    expect(f.host.style.left).toBe("70px");
    f.placed.dispose();
  });

  it("releases placement ownership after a partially installed live binding fails", () => {
    const f = fixture();
    f.layout.place(new Map([[f.zone, 300]]), 70);
    expect(() =>
      f.layout.bind(f.editor, (change) => {
        change();
        throw new Error("geometry failed");
      }),
    ).toThrow();
    expect(f.zones.size).toBe(0);
    expect(f.node.style.top).toBe("300px");
    const live = f.bind();
    live.dispose();
    f.placed.dispose();
  });
});
