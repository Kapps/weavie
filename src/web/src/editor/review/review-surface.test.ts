import { afterEach, describe, expect, it, vi } from "vitest";
import type { ReviewEditor } from "./review-editor";
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
    diff: () => ({
      revision: "1",
      rejected: [],
      path: `/work/${index}.ts`,
      name: `${index}.ts`,
      acceptedBaseline: "",
      acceptedBaselineExists: true,
      baseline: "",
      baselineExists: true,
      current: "changed",
      currentExists: true,
    }),
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

describe("pending review navigation ownership", () => {
  afterEach(() => vi.unstubAllGlobals());

  function painting() {
    const frames: FrameRequestCallback[] = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) =>
      frames.push(callback),
    );
    const current = fixture();
    const section = {
      restore: vi.fn(),
      revealFileStart: vi.fn(),
      focus: vi.fn(),
    } as unknown as ReviewEditor;
    const paint = async () => {
      await Promise.resolve();
      for (const callback of frames.splice(0)) callback(0);
    };
    return { ...current, section, paint };
  }

  it("leaves newer user movement alone when the first diff paint arrives", async () => {
    const { surface, section, paint } = painting();
    surface.reveal("/work/0.ts", 1);
    await paint();
    surface.takeControl();
    surface.sections.set("/work/0.ts", section);
    await Promise.resolve();
    expect(section.restore).not.toHaveBeenCalled();
    expect(section.revealFileStart).not.toHaveBeenCalled();
    expect(section.focus).not.toHaveBeenCalled();
    surface.dispose();
  });

  it("completes a delayed destination exactly once without user takeover", async () => {
    const { surface, section, paint } = painting();
    surface.reveal("/work/0.ts", 80);
    await paint();
    surface.sections.set("/work/0.ts", section);
    surface.sections.set("/work/0.ts", section);
    await Promise.resolve();
    expect(section.restore).toHaveBeenCalledExactlyOnceWith({ path: "/work/0.ts", line: 80 });
    expect(section.focus).toHaveBeenCalledOnce();
    surface.dispose();
  });

  it("allows a new navigation from the input that cancels an older queued frame", async () => {
    const { surface, section, paint } = painting();
    surface.reveal("/work/0.ts", 1);
    await Promise.resolve();
    surface.takeControl();
    surface.reveal("/work/0.ts", 80);
    surface.sections.set("/work/0.ts", section);
    await paint();
    await Promise.resolve();
    expect(section.restore).toHaveBeenCalledExactlyOnceWith({ path: "/work/0.ts", line: 80 });
    expect(section.focus).toHaveBeenCalledOnce();
    surface.dispose();
  });

  it("rejects a presenter restoration when the user takes control", async () => {
    const { surface, section, paint } = painting();
    const result = surface.restore(
      {
        viewState: {
          location: { path: "/work/0.ts", line: 1 },
          scrollTop: 0,
        },
      },
      new AbortController().signal,
    );
    const rejected = expect(result).rejects.toMatchObject({ name: "AbortError" });
    await paint();
    surface.takeControl();
    surface.sections.set("/work/0.ts", section);
    await rejected;
    expect(section.restore).not.toHaveBeenCalled();
    surface.dispose();
  });
});
