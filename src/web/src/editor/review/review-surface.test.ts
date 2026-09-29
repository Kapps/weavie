import { afterEach, describe, expect, it, vi } from "vitest";
import { InteractionIntent } from "../../chrome/interaction-intent";
import { notify } from "../../notify/notify";
import { ReviewHorizontalPositions } from "./review-horizontal-position";
import type { ReviewSection, ReviewSectionState } from "./review-section";
import type { ReviewFileView } from "./review-store";
import { createReviewSurface } from "./review-surface";

vi.mock("../../notify/notify", () => ({ notify: vi.fn() }));

function fixture() {
  const input = new EventTarget();
  const intents = new InteractionIntent(input);
  const state = {
    index: 0 as number | undefined,
    active: true,
    pending: [true, true, true],
    collapsed: false,
  };
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
  const controls = { refresh: vi.fn(), captureActions: vi.fn() };
  const focus = vi.fn();
  const setScrollTop = vi.fn();
  const scrollToIndex = vi.fn();
  const surface = createReviewSurface({
    interaction: intents,
    horizontal: new ReviewHorizontalPositions(vi.fn()),
    controls,
    signal: new AbortController().signal,
    active: () => state.active,
    clear: vi.fn(),
    getScrollTop: () => 0,
    setScrollTop,
    files: () => files,
    currentIndex: () => state.index,
    select,
    expand: vi.fn(),
    scrollToIndex,
    focus,
  });
  return {
    surface: {
      ...surface,
      reveal: (path: string, line: number) => surface.reveal(path, line, intents.begin()),
    },
    input,
    intents,
    files,
    state,
    select,
    controls,
    focus,
    setScrollTop,
    scrollToIndex,
  };
}

function interaction() {
  const navigation = {
    current: vi.fn(() => true),
    restore: vi.fn(),
    revealFileStart: vi.fn(),
    focus: vi.fn(),
  };
  const ready = {
    kind: "ready" as const,
    input: navigation,
    target: { kind: "none" as const },
    enter: vi.fn(() => navigation),
  };
  const state: { value: ReviewSectionState } = { value: ready };
  const section = {
    capture: vi.fn(() => ({ path: "/work/0.ts", line: 80, anchor: { line: 80, offset: -40 } })),
    state: vi.fn(() => state.value),
  } satisfies ReviewSection;
  return { section, sectionState: state, ready, navigation };
}

describe("unified review completion navigation", () => {
  it("does not treat a retained first file as active while only the tree is visible", () => {
    const { surface, state, select, focus, intents } = fixture();
    const { section, ready, navigation } = interaction();
    state.index = undefined;
    surface.sections.bind("/work/0.ts", section);
    expect(surface.capture().text).toBeNull();
    expect(surface.target()).toEqual({ kind: "none" });
    surface.focus();
    expect(focus).toHaveBeenCalledOnce();
    expect(navigation.focus).not.toHaveBeenCalled();
    expect(ready.enter).not.toHaveBeenCalled();
    surface.captureReviewAdvance("/work/0.ts", "keepFile")(
      { path: "/work/1.ts", line: 10 },
      intents.begin(),
      { sourceDeleted: false, sourceHasReview: true },
    );
    expect(select).not.toHaveBeenCalled();
    surface.dispose();
  });

  it("wraps past reviewed files from the decision response, not a snapshot", () => {
    const { surface, state, select, intents } = fixture();
    state.index = 2;
    state.pending[0] = false;
    const complete = surface.captureReviewAdvance("/work/2.ts", "keepFile");
    const focus = intents.begin();
    surface.refresh();
    state.pending[2] = false;
    surface.refresh();
    surface.refresh();
    expect(select).not.toHaveBeenCalled();
    complete({ path: "/work/1.ts", line: 10 }, focus, {
      sourceDeleted: false,
      sourceHasReview: true,
    });
    expect(select).toHaveBeenCalledExactlyOnceWith(1, "/work/1.ts", 10);
    surface.dispose();
  });

  it.each([
    "response first",
    "projection first",
  ])("advances after its source is removed (%s)", (order) => {
    const { surface, state, select, files, intents } = fixture();
    const complete = surface.captureReviewAdvance("/work/0.ts", "keepFile");
    const focus = intents.begin();
    if (order === "projection first") files.shift();
    state.pending[0] = false;
    complete({ path: "/work/1.ts", line: 10 }, focus, {
      sourceDeleted: true,
      sourceHasReview: false,
    });
    expect(select).toHaveBeenCalledExactlyOnceWith(
      order === "projection first" ? 0 : 1,
      "/work/1.ts",
      10,
    );
    surface.dispose();
  });

  it("finishes background data changes without reclaiming newer interaction", () => {
    const { surface, state, select, input, intents } = fixture();
    const complete = surface.captureReviewAdvance("/work/0.ts", "keepFile");
    const focus = intents.begin();
    input.dispatchEvent(new Event("keydown"));
    state.pending[0] = false;
    surface.refresh();
    complete({ path: "/work/1.ts", line: 10 }, focus, {
      sourceDeleted: false,
      sourceHasReview: true,
    });
    expect(select).not.toHaveBeenCalled();
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
    const { surface, state, select, intents } = fixture();
    const complete = surface.captureReviewAdvance("/work/0.ts", "keepFile");
    const focus = intents.begin();
    surface.refresh();
    state.active = false;
    state.pending[0] = false;
    surface.refresh();
    complete({ path: "/work/1.ts", line: 10 }, focus, {
      sourceDeleted: false,
      sourceHasReview: true,
    });
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
  afterEach(() => {
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  function painting() {
    const frames: FrameRequestCallback[] = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) =>
      frames.push(callback),
    );
    const current = fixture();
    const paint = async () => {
      await Promise.resolve();
      for (const callback of frames.splice(0)) callback(0);
      await Promise.resolve();
    };
    return { ...current, ...interaction(), paint };
  }

  it("leaves newer user movement alone when the first diff paint arrives", async () => {
    const { surface, section, navigation, paint } = painting();
    surface.reveal("/work/0.ts", 1);
    await paint();
    surface.takeControl();
    surface.sections.bind("/work/0.ts", section).changed();
    await paint();
    expect(navigation.restore).not.toHaveBeenCalled();
    expect(navigation.revealFileStart).not.toHaveBeenCalled();
    expect(navigation.focus).not.toHaveBeenCalled();
    surface.dispose();
  });

  it("completes a delayed destination exactly once without user takeover", async () => {
    const { surface, section, navigation, paint } = painting();
    surface.reveal("/work/0.ts", 80);
    await paint();
    const binding = surface.sections.bind("/work/0.ts", section);
    binding.changed();
    await paint();
    expect(navigation.restore).toHaveBeenCalledExactlyOnceWith({ path: "/work/0.ts", line: 80 });
    expect(navigation.focus).toHaveBeenCalledOnce();
    surface.dispose();
  });

  it.each([
    "pointerdown",
    "keydown",
    "wheel",
  ])("keeps delayed placement but not focus after newer outside %s input", async (event) => {
    const { surface, section, navigation, input, paint } = painting();
    surface.reveal("/work/0.ts", 80);
    await paint();
    input.dispatchEvent(new Event(event));
    surface.sections.bind("/work/0.ts", section).changed();
    await paint();
    expect(navigation.restore).toHaveBeenCalledExactlyOnceWith({ path: "/work/0.ts", line: 80 });
    expect(navigation.focus).not.toHaveBeenCalled();
    surface.dispose();
  });

  it("waits for a retained ready section to finish preparing fresh geometry", async () => {
    const { surface, section, sectionState, ready, navigation, paint } = painting();
    const binding = surface.sections.bind("/work/0.ts", section);
    sectionState.value = { kind: "pending", input: undefined };
    binding.changed();
    surface.reveal("/work/0.ts", 80);
    await paint();
    expect(ready.enter).not.toHaveBeenCalled();
    expect(navigation.restore).not.toHaveBeenCalled();
    expect(navigation.focus).not.toHaveBeenCalled();
    sectionState.value = ready;
    binding.changed();
    binding.changed();
    surface.refresh();
    expect(ready.enter).toHaveBeenCalledExactlyOnceWith("navigate");
    expect(navigation.restore).toHaveBeenCalledExactlyOnceWith({ path: "/work/0.ts", line: 80 });
    expect(navigation.focus).toHaveBeenCalledOnce();
    surface.dispose();
  });

  it("keeps restoration focus-free even when ambient recovery runs before preparation", async () => {
    const { surface, section, sectionState, ready, navigation, select, paint } = painting();
    sectionState.value = { kind: "pending", input: undefined };
    const binding = surface.sections.bind("/work/0.ts", section);
    const location = section.capture();
    const result = surface.restore(
      { viewState: { location, scrollTop: 450, horizontal: {} } },
      new AbortController().signal,
    );
    const restored = expect(result).resolves.toBeUndefined();
    await paint();
    surface.focus();
    surface.focus();
    expect(navigation.restore).not.toHaveBeenCalled();
    expect(navigation.focus).not.toHaveBeenCalled();
    sectionState.value = ready;
    binding.changed();
    await restored;
    expect(select).toHaveBeenCalledExactlyOnceWith(0, "/work/0.ts", 80);
    expect(navigation.restore).toHaveBeenCalledExactlyOnceWith(location);
    expect(navigation.focus).not.toHaveBeenCalled();
    surface.focus();
    expect(navigation.focus).toHaveBeenCalledOnce();
    surface.dispose();
  });

  for (const kind of ["ready", "pending"] as const) {
    it(`keeps ambient focus passive when the ${kind} section has no live input`, () => {
      const { surface, section, sectionState, ready, navigation, focus, select } = painting();
      sectionState.value =
        kind === "ready" ? { ...ready, input: undefined } : { kind, input: undefined };
      surface.sections.bind("/work/0.ts", section);
      surface.focus();
      expect(focus).toHaveBeenCalledOnce();
      expect(ready.enter).not.toHaveBeenCalled();
      expect(navigation.focus).not.toHaveBeenCalled();
      expect(select).not.toHaveBeenCalled();
      surface.dispose();
    });
  }

  it("returns ambient focus to existing live input without reacquiring navigation", () => {
    const { surface, section, ready, navigation, focus, select } = painting();
    surface.sections.bind("/work/0.ts", section);
    surface.focus();
    expect(navigation.focus).toHaveBeenCalledOnce();
    expect(ready.enter).not.toHaveBeenCalled();
    expect(focus).not.toHaveBeenCalled();
    expect(select).not.toHaveBeenCalled();
    surface.dispose();
  });

  it("retries failed preparation only when the user selects the file again", async () => {
    const { surface, section, sectionState, ready, navigation, paint } = painting();
    sectionState.value = { kind: "pending", input: undefined };
    const binding = surface.sections.bind("/work/0.ts", section);
    const retry = vi.fn(() => {
      sectionState.value = { kind: "pending", input: undefined };
      binding.changed();
    });
    surface.reveal("/work/0.ts", 80);
    await paint();
    sectionState.value = {
      kind: "unavailable",
      input: undefined,
      target: ready.target,
      failure: { error: new Error("grammar failed"), retry },
    };
    binding.changed();
    await paint();
    surface.refresh();
    expect(retry).not.toHaveBeenCalled();
    expect(navigation.focus).not.toHaveBeenCalled();
    expect(notify).toHaveBeenCalledExactlyOnceWith("warn", "Error: grammar failed");
    surface.reveal("/work/0.ts", 80);
    expect(retry).toHaveBeenCalledOnce();
    await paint();
    sectionState.value = ready;
    binding.changed();
    expect(navigation.restore).toHaveBeenCalledExactlyOnceWith({ path: "/work/0.ts", line: 80 });
    expect(navigation.focus).toHaveBeenCalledOnce();
    surface.dispose();
  });

  it("keeps the exact failed file's unavailable toolbar target", () => {
    const { surface, section, sectionState } = painting();
    const target = { kind: "none" as const };
    sectionState.value = {
      kind: "unavailable",
      input: undefined,
      target,
      failure: { error: new Error("paint failed"), retry: vi.fn() },
    };
    const binding = surface.sections.bind("/work/0.ts", section);
    expect(surface.target()).toBe(target);
    binding.dispose();
    section.state.mockClear();
    expect(surface.target()).toEqual({ kind: "none" });
    expect(section.state).not.toHaveBeenCalled();
    surface.dispose();
  });

  it("parks collapsed file actions without releasing the registered reading location", () => {
    const { surface, section, sectionState, ready } = painting();
    const location = section.capture();
    surface.sections.bind("/work/0.ts", section);
    expect(surface.target()).toBe(ready.target);
    sectionState.value = { kind: "collapsed" };
    expect(surface.target()).toEqual({ kind: "none" });
    expect(surface.capture().text).toEqual(location);
    sectionState.value = ready;
    expect(surface.target()).toBe(ready.target);
    surface.dispose();
  });

  it("restores a collapsed file's saved outer position without restoring its hidden editor", async () => {
    const {
      surface,
      section,
      sectionState,
      state,
      ready,
      navigation,
      focus,
      setScrollTop,
      scrollToIndex,
    } = painting();
    state.collapsed = true;
    sectionState.value = { kind: "collapsed" };
    surface.sections.bind("/work/0.ts", section);
    await surface.restore(
      { viewState: { location: section.capture(), scrollTop: 450, horizontal: {} } },
      new AbortController().signal,
    );
    surface.focus();
    expect(setScrollTop).toHaveBeenCalledExactlyOnceWith(450);
    expect(scrollToIndex).not.toHaveBeenCalled();
    expect(ready.enter).not.toHaveBeenCalled();
    expect(navigation.restore).not.toHaveBeenCalled();
    expect(navigation.focus).not.toHaveBeenCalled();
    expect(focus).toHaveBeenCalledOnce();
    surface.dispose();
  });

  it("keeps a failed retry recoverable without retrying it in the background", async () => {
    const { surface, section, sectionState, ready, navigation, paint } = painting();
    const retry = vi.fn<() => void>(() => {
      throw new Error("still unavailable");
    });
    sectionState.value = {
      kind: "unavailable",
      input: undefined,
      target: ready.target,
      failure: { error: new Error("unavailable"), retry },
    };
    const binding = surface.sections.bind("/work/0.ts", section);
    surface.reveal("/work/0.ts", 80);
    await paint();
    surface.refresh();
    expect(retry).toHaveBeenCalledOnce();
    expect(notify).toHaveBeenCalledExactlyOnceWith("warn", "Error: still unavailable");
    retry.mockImplementation(() => {
      sectionState.value = ready;
      binding.changed();
    });
    surface.reveal("/work/0.ts", 80);
    await paint();
    expect(retry).toHaveBeenCalledTimes(2);
    expect(navigation.focus).toHaveBeenCalledOnce();
    surface.dispose();
  });

  it("releases the retired file's retry without clearing a replacement owner's failure", async () => {
    const { surface, section, sectionState, ready, paint } = painting();
    const retiredRetry = vi.fn();
    sectionState.value = {
      kind: "unavailable",
      input: undefined,
      target: ready.target,
      failure: { error: new Error("failed"), retry: retiredRetry },
    };
    const binding = surface.sections.bind("/work/0.ts", section);
    binding.dispose();
    surface.reveal("/work/0.ts", 80);
    await paint();
    expect(retiredRetry).not.toHaveBeenCalled();
    surface.takeControl();
    const replacement = interaction();
    const retry = vi.fn();
    replacement.sectionState.value = {
      kind: "unavailable",
      input: undefined,
      target: replacement.ready.target,
      failure: { error: new Error("replacement failed"), retry },
    };
    surface.sections.bind("/work/0.ts", replacement.section);
    binding.dispose();
    binding.changed();
    surface.reveal("/work/0.ts", 80);
    expect(retry).toHaveBeenCalledOnce();
    expect(retiredRetry).not.toHaveBeenCalled();
    surface.dispose();
  });

  it("allows a new navigation from the input that cancels an older queued frame", async () => {
    const { surface, section, navigation, paint } = painting();
    surface.reveal("/work/0.ts", 1);
    await Promise.resolve();
    surface.takeControl();
    surface.reveal("/work/0.ts", 80);
    surface.sections.bind("/work/0.ts", section);
    await paint();
    expect(navigation.restore).toHaveBeenCalledExactlyOnceWith({ path: "/work/0.ts", line: 80 });
    expect(navigation.focus).toHaveBeenCalledOnce();
    surface.dispose();
  });

  it("rejects a presenter restoration when the user takes control", async () => {
    const { surface, section, navigation, paint } = painting();
    const result = surface.restore(
      {
        viewState: {
          location: { path: "/work/0.ts", line: 1 },
          scrollTop: 0,
          horizontal: {},
        },
      },
      new AbortController().signal,
    );
    const rejected = expect(result).rejects.toMatchObject({ name: "AbortError" });
    await paint();
    surface.takeControl();
    surface.sections.bind("/work/0.ts", section);
    await rejected;
    expect(navigation.restore).not.toHaveBeenCalled();
    surface.dispose();
  });

  for (const kind of ["pending", "unavailable"] as const) {
    it(`focuses existing ${kind} input without waiting for navigation geometry`, () => {
      const { surface, section, sectionState, ready, navigation, scrollToIndex } = painting();
      const retry = vi.fn();
      sectionState.value =
        kind === "pending"
          ? { kind, input: navigation }
          : {
              kind,
              input: navigation,
              target: ready.target,
              failure: { error: new Error("paint failed"), retry },
            };
      const binding = surface.sections.bind("/work/0.ts", section);
      surface.requestFocus("/work/0.ts");
      binding.changed();
      expect(navigation.focus).toHaveBeenCalledOnce();
      expect(navigation.restore).not.toHaveBeenCalled();
      expect(ready.enter).not.toHaveBeenCalled();
      expect(scrollToIndex).not.toHaveBeenCalled();
      expect(retry).not.toHaveBeenCalled();
      surface.dispose();
    });
  }

  for (const during of ["enter", "restore"] as const) {
    it(`does not focus after a reentrant user cancellation during ${during}`, async () => {
      const { surface, section, ready, navigation, paint } = painting();
      surface.sections.bind("/work/0.ts", section);
      if (during === "enter")
        ready.enter.mockImplementation(() => {
          surface.takeControl();
          return navigation;
        });
      else navigation.restore.mockImplementation(() => surface.takeControl());
      surface.reveal("/work/0.ts", 80);
      await paint();
      expect(navigation.restore).toHaveBeenCalledTimes(during === "restore" ? 1 : 0);
      expect(navigation.focus).not.toHaveBeenCalled();
      expect(notify).not.toHaveBeenCalled();
      surface.dispose();
    });
  }

  for (const during of ["restore", "focus"] as const) {
    it(`finishes a newer focus request created reentrantly during ${during}`, async () => {
      const { surface, section, navigation, paint } = painting();
      const replacement = interaction();
      surface.sections.bind("/work/0.ts", section);
      surface.sections.bind("/work/1.ts", replacement.section);
      navigation[during].mockImplementation(() => surface.requestFocus("/work/1.ts"));
      surface.reveal("/work/0.ts", 80);
      await paint();
      expect(replacement.ready.enter).toHaveBeenCalledExactlyOnceWith("focus");
      expect(replacement.navigation.focus).toHaveBeenCalledOnce();
      expect(replacement.navigation.restore).not.toHaveBeenCalled();
      expect(navigation.focus).toHaveBeenCalledTimes(during === "focus" ? 1 : 0);
      surface.dispose();
    });
  }

  it("cancels a replaced owner without allowing its stale lease to cancel the replacement", async () => {
    const { surface, section, sectionState, ready, navigation, controls, paint } = painting();
    sectionState.value = { kind: "pending", input: undefined };
    const stale = surface.sections.bind("/work/0.ts", section);
    const result = surface.restore(
      { viewState: { location: { path: "/work/0.ts", line: 1 }, scrollTop: 0, horizontal: {} } },
      new AbortController().signal,
    );
    const rejected = expect(result).rejects.toMatchObject({ name: "AbortError" });
    await paint();
    const replacement = interaction();
    replacement.sectionState.value = { kind: "pending", input: undefined };
    const current = surface.sections.bind("/work/0.ts", replacement.section);
    await rejected;
    surface.reveal("/work/0.ts", 80);
    await paint();
    controls.refresh.mockClear();
    section.capture.mockClear();
    replacement.section.capture.mockClear();
    sectionState.value = ready;
    stale.changed();
    stale.dispose();
    expect(controls.refresh).not.toHaveBeenCalled();
    expect(section.capture).not.toHaveBeenCalled();
    expect(replacement.section.capture).not.toHaveBeenCalled();
    expect(navigation.restore).not.toHaveBeenCalled();
    expect(navigation.focus).not.toHaveBeenCalled();
    replacement.sectionState.value = replacement.ready;
    current.changed();
    expect(replacement.navigation.restore).toHaveBeenCalledExactlyOnceWith({
      path: "/work/0.ts",
      line: 80,
    });
    expect(replacement.navigation.focus).toHaveBeenCalledOnce();
    expect(surface.target()).toBe(replacement.ready.target);
    surface.dispose();
  });
});
