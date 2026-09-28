import { describe, expect, it, vi } from "vitest";
import { createAdaptiveReviewSection } from "./review-adaptive-section";
import type { ReviewEditor } from "./review-editor";
import type {
  PassiveReviewPresentation,
  PreparedPassiveReview,
} from "./review-passive-presentation";

vi.mock("../../commands/keybindings", () => ({ IS_MAC: false }));

function fixture() {
  const options = { mode: "applied", original: "before" };
  const prepared = {
    document: { actions: { options } },
    source: { markers: {} },
    rendered: {
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
    prepared: prepared as PreparedPassiveReview | undefined,
    live: undefined as ReviewEditor | undefined,
    valid: true,
    error: "",
  };
  const reveal = vi.fn((top: number) => {
    state.top = top;
  });
  const passive = {
    current: () => state.prepared,
    displayed: () => prepared,
    document: () => prepared.document,
    error: () => state.error,
    bounds: () => ({ top: state.top, bottom: state.top + 200, height: 200 }),
    reveal,
  } as unknown as PassiveReviewPresentation;
  const focus = vi.fn();
  const activate = vi.fn(() => ({ focus }) as unknown as ReviewEditor);
  const section = createAdaptiveReviewSection({
    path: "/file.ts",
    scope: { current: "change" },
    valid: () => state.valid,
    passive: () => passive,
    editor: () => state.live,
    activate,
    cursor: () => state.cursor,
    select: (line) => {
      state.cursor = line;
    },
    viewState: () => null,
    save: vi.fn(),
    comments: { presenter: { open: vi.fn(), focused: () => false } } as unknown as Parameters<
      typeof createAdaptiveReviewSection
    >[0]["comments"],
  });
  return { section, state, prepared, reveal, activate, focus };
}

describe("passive review section parity", () => {
  it("restores the viewport anchor of an unactivated file, not its summary line", () => {
    const f = fixture();
    const saved = f.section.capture();
    expect(saved).toMatchObject({ line: 18, anchor: { line: 18, offset: -90 } });
    f.state.top = 0;
    f.section.restore(saved);
    expect(f.state.top).toBe(250);
    expect(f.activate).not.toHaveBeenCalled();
    f.section.focus();
    expect(f.activate).toHaveBeenCalledOnce();
    expect(f.focus).toHaveBeenCalledOnce();
  });

  it("keeps passive action targets guarded by exact prepared and configuration identities", () => {
    const f = fixture();
    const target = f.section.target();
    expect(target.kind).toBe("file");
    if (target.kind !== "file") throw new Error("Missing passive file target");
    expect(target.presentation.valid()).toBe(true);
    expect(target.presentation.reviewLine()).toBe(18);
    f.state.prepared = undefined;
    expect(target.presentation.valid()).toBe(false);
    expect(f.section.target()).toEqual({ kind: "none" });
    expect(f.section.capture().anchor).toEqual({ line: 18, offset: -90 });
    f.state.prepared = f.prepared;
    f.state.valid = false;
    expect(target.presentation.valid()).toBe(false);
    expect(f.activate).not.toHaveBeenCalled();
  });

  it("uses a visible saved cursor and delegates to the live editor without changing ownership", () => {
    const f = fixture();
    f.state.cursor = 15;
    expect(f.section.capture().line).toBe(15);
    const location = { path: "/file.ts", line: 80 };
    const live = {
      capture: vi.fn(() => location),
      restore: vi.fn(),
      target: vi.fn(() => ({ kind: "none" })),
    };
    f.state.live = live as unknown as ReviewEditor;
    expect(f.section.capture()).toBe(location);
    f.section.restore(location);
    expect(live.restore).toHaveBeenCalledExactlyOnceWith(location);
    expect(f.activate).not.toHaveBeenCalled();
  });

  it("preserves unavailable whole-file commands without treating failed paint as ready", () => {
    const f = fixture();
    f.state.prepared = undefined;
    f.state.error = "Diff worker failed";
    const target = f.section.target();
    if (target.kind !== "file") throw new Error("Missing unavailable file target");
    expect(target.paint).toMatchObject({ status: "unavailable", message: "Diff worker failed" });
    expect(target.document).toBe(f.prepared.document);
    expect(target.presentation.availability()).toBe("unavailable");
    expect(target.presentation.valid()).toBe(true);
    f.state.error = "";
    expect(target.presentation.valid()).toBe(false);
    expect(f.section.target()).toEqual({ kind: "none" });
  });
});
