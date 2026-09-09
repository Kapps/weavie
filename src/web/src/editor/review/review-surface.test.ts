import { describe, expect, it, vi } from "vitest";
import type { ReviewFileView } from "./review-store";
import { createReviewSurface } from "./review-surface";

vi.mock("../../notify/notify", () => ({ notify: vi.fn() }));

function fixture() {
  const state = { index: 0, active: true, pending: [true, true, true], collapsed: false };
  const files: ReviewFileView[] = state.pending.map((_, index) => ({
    summary: () => ({
      path: `/work/${index}.ts`,
      name: `${index}.ts`,
      line: 10,
      added: 1,
      removed: 0,
      currentExists: true,
    }),
    pending: () => state.pending[index]!,
    loaded: () => true,
    collapsed: () => state.collapsed,
    diff: () => null,
    comments: () => null,
  }));
  const select = vi.fn((index: number) => {
    state.index = index;
  });
  const surface = createReviewSurface({
    signal: new AbortController().signal,
    active: () => state.active,
    changed: vi.fn(),
    clear: vi.fn(),
    scroller: () => ({ scrollTop: 0 }) as HTMLElement,
    files: () => files,
    currentIndex: () => state.index,
    select,
    expand: vi.fn(),
    scrollToIndex: vi.fn(),
    focus: vi.fn(),
  });
  return { surface, state, select };
}

describe("unified review completion navigation", () => {
  it("wraps past reviewed files and advances only once for a completion", () => {
    const { surface, state, select } = fixture();
    state.index = 2;
    state.pending[0] = false;
    surface.refresh();
    state.pending[2] = false;
    surface.refresh();
    surface.refresh();
    expect(select).toHaveBeenCalledExactlyOnceWith(1, "/work/1.ts", 10);
    surface.dispose();
  });

  it("does not navigate for a manual collapse or when no pending file remains", () => {
    const { surface, state, select } = fixture();
    surface.refresh();
    state.collapsed = true;
    surface.refresh();
    expect(select).not.toHaveBeenCalled();
    state.pending.fill(false);
    surface.refresh();
    expect(select).not.toHaveBeenCalled();
    surface.dispose();
  });

  it("does not replay a completion that arrived while another session or tab was active", () => {
    const { surface, state, select } = fixture();
    surface.refresh();
    state.active = false;
    state.pending[0] = false;
    surface.refresh();
    state.active = true;
    surface.refresh();
    expect(select).not.toHaveBeenCalled();
    surface.dispose();
  });

  it("does not double-advance when hunk navigation already moved to another file", () => {
    const { surface, state, select } = fixture();
    surface.refresh();
    state.index = 1;
    state.pending[0] = false;
    surface.refresh();
    expect(select).not.toHaveBeenCalled();
    surface.dispose();
  });
});
