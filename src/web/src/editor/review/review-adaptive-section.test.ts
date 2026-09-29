import { describe, expect, it, vi } from "vitest";
import type { TextLocation } from "../nav-history";
import { createAdaptiveReviewSection } from "./review-adaptive-section";
import type { ReviewEditor } from "./review-editor";
import type {
  PassiveReviewPresentation,
  PreparedPassiveReview,
} from "./review-passive-presentation";
import type { ReviewSectionFailure } from "./review-section";
import type { ReviewToolbarTarget } from "./review-toolbar-state";

vi.mock("../../commands/keybindings", () => ({ IS_MAC: false }));

function fixture() {
  const configuration = { mode: "applied", original: "before" };
  const markers = {};
  const prepared = {
    document: { actions: { options: configuration, stale: false, geometry: { markers } } },
    source: { markers },
    rendered: {
      height: 2_000,
      lineHeight: 20,
      geometry: {
        topForLineNumber: (line: number) => (line - 1) * 20,
        lineAtOffset: (top: number) => Math.floor(top / 20) + 1,
      },
    },
  } as unknown as PreparedPassiveReview;
  const state = {
    top: 250,
    cursor: 1,
    valid: true,
    collapsed: false,
    empty: false,
    stale: false,
    prepared: prepared as PreparedPassiveReview | undefined,
    live: undefined as ReviewEditor | undefined,
    failure: undefined as ReviewSectionFailure | undefined,
    paint: "ready" as "ready" | "pending" | "unavailable",
  };
  const reveal = vi.fn((top: number) => {
    state.top = top;
  });
  const bounds = vi.fn(() => ({ top: state.top, bottom: state.top + 200, height: 200 }));
  const current = vi.fn(() => state.prepared);
  const passive = {
    prepared: () => state.prepared,
    current,
    displayed: () => prepared,
    document: () => prepared.document,
    error: () => (state.failure ? String(state.failure.error) : ""),
    failure: () => state.failure,
    bounds,
    reveal,
  } as unknown as PassiveReviewPresentation;
  const focus = vi.fn();
  const target = (): ReviewToolbarTarget => ({
    kind: "file",
    owner: live,
    document: prepared.document,
    presentation: { valid: () => !state.stale } as Extract<
      ReviewToolbarTarget,
      { kind: "file" }
    >["presentation"],
    paint:
      state.paint === "ready"
        ? {
            status: "ready",
            options: prepared.document.actions.options!,
            markers: prepared.source.markers,
          }
        : state.paint === "pending"
          ? { status: "pending" }
          : {
              status: "unavailable",
              options: prepared.document.actions.options!,
              message: "failed",
            },
  });
  const live = {
    focus,
    target,
    ready: () => true,
    retry: vi.fn(),
    capture: vi.fn(() => ({ path: "/file.ts", line: 80 })),
    restore: vi.fn((location: TextLocation) => {
      state.cursor = location.line;
      state.prepared = undefined;
      if (location.anchor) state.top = (location.anchor.line - 1) * 20 + location.anchor.offset;
    }),
    revealFileStart: vi.fn(),
  } as unknown as ReviewEditor;
  const activate = vi.fn(() => {
    state.live ??= live;
    return state.live;
  });
  const section = createAdaptiveReviewSection({
    path: "/file.ts",
    scope: { current: "change" },
    valid: () => state.valid,
    collapsed: () => state.collapsed,
    empty: () => state.empty,
    failure: () => undefined,
    passive: () => passive,
    editor: () => state.live,
    activate,
    cursor: () => state.cursor,
    select: (line) => {
      state.cursor = line;
      state.prepared = undefined;
    },
    viewState: () => null,
    comments: { presenter: { open: vi.fn(), focused: () => false } } as unknown as Parameters<
      typeof createAdaptiveReviewSection
    >[0]["comments"],
  });
  const enter = (intent: "focus" | "navigate") => {
    const value = section.state();
    if (value.kind !== "ready") throw new Error(`Unexpected state: ${value.kind}`);
    const navigation = value.enter(intent);
    if (!navigation) throw new Error("Missing current navigation capability");
    return navigation;
  };
  return {
    section,
    state,
    prepared,
    passive,
    reveal,
    activate,
    focus,
    bounds,
    current,
    live,
    enter,
  };
}

describe("adaptive review section authority", () => {
  it("activates before restoring a cursor-dependent passive projection", () => {
    const f = fixture();
    const saved = f.section.capture();
    expect(saved).toMatchObject({ line: 18, anchor: { line: 18, offset: -90 } });
    f.state.top = 0;
    const navigation = f.enter("navigate");
    expect(f.activate).toHaveBeenCalledOnce();
    expect(f.live.restore).not.toHaveBeenCalled();
    navigation.restore(saved);
    expect(f.state.prepared).toBeUndefined();
    expect(navigation.current()).toBe(true);
    navigation.focus();
    expect(f.state.top).toBe(250);
    expect(f.focus).toHaveBeenCalledOnce();
  });

  it("observes readiness without validating, publishing, or restarting preparation", () => {
    const f = fixture();
    expect(f.section.state().kind).toBe("ready");
    const target = f.section.passiveTarget();
    if (target.kind !== "file") throw new Error("Missing passive target");
    expect(target.presentation.valid()).toBe(true);
    expect(target.presentation.reviewLine()).toBe(18);
    f.state.prepared = undefined;
    expect(target.presentation.valid()).toBe(false);
    expect(f.section.state()).toEqual({ kind: "pending", input: undefined });
    expect(f.section.capture().anchor).toEqual({ line: 18, offset: -90 });
    expect(f.current).not.toHaveBeenCalled();
    expect(f.activate).not.toHaveBeenCalled();
  });

  it("revokes a captured passive entry when readiness or ownership changes", () => {
    const f = fixture();
    const state = f.section.state();
    if (state.kind !== "ready") throw new Error("Missing ready section");
    f.state.prepared = undefined;
    expect(state.enter("navigate")).toBeUndefined();
    f.state.prepared = f.prepared;
    f.state.valid = false;
    expect(state.enter("navigate")).toBeUndefined();
    expect(f.activate).not.toHaveBeenCalled();
  });

  it("retains capture but cannot navigate collapsed or empty files", () => {
    const f = fixture();
    f.state.cursor = 15;
    f.state.collapsed = true;
    expect(f.section.capture().line).toBe(15);
    expect(f.section.state()).toEqual({ kind: "collapsed" });
    f.state.live = f.live;
    expect(f.section.capture().line).toBe(80);
    expect(f.section.state()).toEqual({ kind: "collapsed" });
    f.state.collapsed = false;
    f.state.empty = true;
    expect(f.section.state()).toEqual({ kind: "empty" });
    expect(f.activate).not.toHaveBeenCalled();
  });

  it("preserves explicit failure and unavailable whole-file commands without granting navigation", () => {
    const f = fixture();
    f.state.prepared = undefined;
    const failure = { error: new Error("grammar failed"), retry: vi.fn() };
    f.state.failure = failure;
    const state = f.section.state();
    expect(state.kind).toBe("unavailable");
    if (state.kind !== "unavailable" || state.target.kind !== "file")
      throw new Error("Missing failure");
    expect(state.failure).toBe(failure);
    expect(state.target.paint).toMatchObject({
      status: "unavailable",
      message: "Error: grammar failed",
    });
    expect(state.target.presentation.valid()).toBe(true);
    f.state.failure = undefined;
    expect(state.target.presentation.valid()).toBe(false);
    expect(f.section.state().kind).toBe("pending");
    expect(failure.retry).not.toHaveBeenCalled();
  });

  it.each([
    "pending",
    "unavailable",
  ] as const)("keeps live focus independent of %s passive geometry", (paint) => {
    const f = fixture();
    f.state.live = f.live;
    f.state.paint = paint;
    f.state.prepared = undefined;
    f.bounds.mockReturnValue({ top: 250, bottom: 0, height: 200 });
    const state = f.section.state();
    if (state.kind !== "pending" && state.kind !== "unavailable")
      throw new Error("Unexpected navigation");
    state.input!.focus();
    expect(f.focus).toHaveBeenCalledOnce();
    expect(f.bounds).not.toHaveBeenCalled();
    expect(f.reveal).not.toHaveBeenCalled();
    expect(f.state.top).toBe(250);
  });

  it("ignores initial paint readiness when current geometry is stale", () => {
    const f = fixture();
    f.state.live = f.live;
    f.state.stale = true;
    expect(f.live.ready()).toBe(true);
    expect(f.section.state().kind).toBe("pending");
    f.state.stale = false;
    const navigation = f.enter("navigate");
    f.state.live = { ...f.live };
    navigation.restore({ path: "/file.ts", line: 4 });
    navigation.focus();
    expect(navigation.current()).toBe(false);
    expect(f.live.restore).not.toHaveBeenCalled();
    expect(f.focus).not.toHaveBeenCalled();
  });

  it.each([
    -300, 2_100,
  ])("reveals an offscreen passive file before keyboard activation at %s", (top) => {
    const f = fixture();
    f.state.top = top;
    f.enter("focus").focus();
    expect(f.reveal).toHaveBeenCalledExactlyOnceWith(0);
    expect(f.focus).toHaveBeenCalledOnce();
  });
});
