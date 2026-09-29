import { describe, expect, it, vi } from "vitest";
import type { InlineDiffOptions } from "../inline-diff";
import type { monaco } from "../monaco-setup";
import type { AcceptedDiffHunk, DiffHunk } from "./diff-markers";
import type { ReviewCommentContext } from "./review-comment-session";
import type { ReviewGeometry, ReviewPreparation } from "./review-document";
import { type ReviewActionPresentation, ReviewFileActions } from "./review-file-actions";
import { reviewDiffSources } from "./review-sources";

function fixture() {
  let version = 1;
  let alive = true;
  let line = 1;
  let valid = true;
  let composing = false;
  let availability: ReturnType<ReviewActionPresentation["availability"]> = "ready";
  const model = {
    isDisposed: () => !alive,
    getVersionId: () => version,
    getLinesContent: () => ["one", "two", "three", "four"],
  } as unknown as monaco.editor.ITextModel;
  const options: InlineDiffOptions = {
    mode: "applied",
    original: "before",
    claudeVersion: "agent",
    onKeepHunk: vi.fn(),
    onRevertHunk: vi.fn(),
    onUnkeepHunk: vi.fn(),
    onKeepFile: vi.fn(),
    onRevertFile: vi.fn(),
    onKeepAll: vi.fn(),
    onUndo: vi.fn(),
    onNextFile: vi.fn(),
    onPrevFile: vi.fn(),
    commenting: {} as ReviewCommentContext,
  };
  const hunk: DiffHunk = {
    anchorLine: 2,
    baselineStart: 1,
    baselineEndExclusive: 2,
    currentStart: 2,
    currentEndExclusive: 4,
  };
  const accepted: AcceptedDiffHunk = {
    anchorLine: 1,
    acceptedStart: 1,
    acceptedEndExclusive: 2,
    reviewStart: 1,
    reviewEndExclusive: 2,
    acceptedGuardText: "old",
    guardText: "kept",
  };
  let prepared: ReviewPreparation | undefined;
  let configured: Readonly<InlineDiffOptions> | undefined = options;
  const configure = (value: InlineDiffOptions | undefined): void => {
    configured = value;
  };
  const actions = new ReviewFileActions(
    model,
    () => prepared,
    () => configured,
  );
  const publish = (value: InlineDiffOptions, hunks: DiffHunk[]): ReviewGeometry => {
    const sources = reviewDiffSources(value);
    const geometry: ReviewGeometry = {
      status: "ready",
      model,
      version,
      sources,
      markers: { hunks, acceptedHunks: [accepted], decorations: [], ghosts: [], isNewFile: false },
      collapsed: { hidden: [], gapMarkers: [] },
    };
    prepared = { version, sources, result: geometry };
    return geometry;
  };
  publish(options, [hunk]);
  const port: ReviewActionPresentation = {
    scope: { current: "change" },
    valid: () => valid,
    availability: () => availability,
    reviewLine: () => line,
    commentLine: () => 3,
    revealLine: vi.fn(),
    selectLine: vi.fn(),
    openComment: vi.fn(),
    composerFocused: () => composing,
    swallowFileNavigation: false,
  };
  return {
    actions,
    configure,
    options,
    port,
    model,
    hunk,
    accepted,
    publish,
    edit: () => version++,
    closeModel: () => {
      alive = false;
    },
    rebind: () => {
      valid = false;
    },
    move: () => {
      line = 4;
    },
    compose: () => {
      composing = true;
    },
    unavailable: () => {
      availability = "unavailable";
    },
    fail: () => {
      prepared = { version, sources: reviewDiffSources(options), result: { status: "timed-out" } };
    },
  };
}

describe("document-owned review actions", () => {
  it("uses the same guarded payload from passive and active presentation ports", () => {
    const f = fixture();
    const active = { ...f.port, revealLine: vi.fn() };
    expect(f.actions.captureHunk(f.hunk, f.port).keep()).toBe(true);
    expect(f.actions.captureHunk(f.hunk, active).revert()).toBe(true);
    const expected = {
      baselineStart: 1,
      baselineEndExclusive: 2,
      currentStart: 2,
      currentEndExclusive: 4,
      guardText: "two\nthree",
    };
    expect(f.options.onKeepHunk).toHaveBeenCalledWith(expected);
    expect(f.options.onRevertHunk).toHaveBeenCalledWith(expected);
    expect(f.options.onNextFile).not.toHaveBeenCalled();
  });

  it("reveals the next remaining hunk and does not advance the file early", () => {
    const f = fixture();
    const next = { ...f.hunk, anchorLine: 4 };
    f.publish(f.options, [f.hunk, next]);
    f.actions.commands(f.port).accept();
    expect(f.port.revealLine).toHaveBeenCalledWith(4);
    expect(f.options.onNextFile).not.toHaveBeenCalled();
  });

  it.each([
    false,
    true,
  ])("leaves cross-file advancement to the decision result with faded band: %s", (faded) => {
    const f = fixture();
    const options = { ...f.options, acceptedBaseline: faded ? "anchor" : f.options.original };
    f.configure(options);
    f.publish(options, [f.hunk]);
    expect(f.actions.commands(f.port).reject()).toBe(true);
    expect(f.options.onNextFile).not.toHaveBeenCalled();
  });

  it.each([
    "edit",
    "closeModel",
    "rebind",
    "move",
    "scope",
    "options",
    "clear",
    "geometry",
    "dispose",
    "unavailable",
  ] as const)("invalidates a captured presentation command on %s", (reason) => {
    const f = fixture();
    const captured = f.actions.capture(f.port);
    if (reason === "scope") f.port.scope.current = "file";
    else if (reason === "options") f.configure({ ...f.options });
    else if (reason === "clear") {
      f.configure(undefined);
    } else if (reason === "geometry") f.publish(f.options, [{ ...f.hunk }]);
    else if (reason === "dispose") f.actions.dispose();
    else f[reason]();
    expect(() => captured.accept()).toThrow("location");
    expect(f.options.onKeepHunk).not.toHaveBeenCalled();
  });

  it("invalidates old hunk controls when same-source callbacks change", () => {
    const f = fixture();
    const hunk = f.actions.captureHunk(f.hunk, f.port);
    const unkeep = f.actions.captureUnkeep(f.accepted, f.port);
    const next = { ...f.options, onKeepHunk: vi.fn(), onUnkeepHunk: vi.fn() };
    const geometry = f.actions.geometry;
    f.configure(next);
    expect(f.actions.geometry).toBe(geometry);
    expect(f.actions.stale).toBe(false);
    expect(hunk.keep()).toBe(false);
    expect(unkeep()).toBe(false);
    expect(f.actions.captureHunk(f.hunk, f.port).keep()).toBe(true);
    expect(next.onKeepHunk).toHaveBeenCalledOnce();
    expect(f.options.onKeepHunk).not.toHaveBeenCalled();
  });

  it("does not accept a late computation for superseded configuration", () => {
    const f = fixture();
    f.configure({ ...f.options, original: "new baseline" });
    f.publish(f.options, [f.hunk]);
    expect(f.actions.stale).toBe(true);
    expect(f.actions.commands(f.port).accept()).toBe(true);
    expect(f.options.onKeepHunk).not.toHaveBeenCalled();
  });

  it("guards accepted-hunk payloads and preserves their exact anchor", () => {
    const f = fixture();
    expect(f.actions.captureUnkeep(f.accepted, f.port)()).toBe(true);
    expect(f.port.selectLine).toHaveBeenCalledWith(1);
    expect(f.options.onUnkeepHunk).toHaveBeenCalledWith({
      acceptedStart: 1,
      acceptedEndExclusive: 2,
      reviewStart: 1,
      reviewEndExclusive: 2,
      acceptedGuardText: "old",
      guardText: "kept",
    });
    const old = f.actions.captureUnkeep(f.accepted, f.port);
    f.edit();
    expect(old()).toBe(false);
  });

  it("consumes fully kept hunk chords without editing or advancing", () => {
    const f = fixture();
    const options = { ...f.options, acceptedBaseline: "anchor", original: "agent" };
    f.configure(options);
    f.publish(options, []);
    const commands = f.actions.commands(f.port);
    expect(commands.accept()).toBe(true);
    expect(commands.reject()).toBe(true);
    expect(commands.keepFile()).toBe(false);
    expect(f.options.onNextFile).not.toHaveBeenCalled();
    expect(f.options.onKeepHunk).not.toHaveBeenCalled();
  });

  it("declines file commands after the diff becomes genuinely empty", () => {
    const f = fixture();
    f.publish(f.options, []);
    const commands = f.actions.commands(f.port);
    for (const command of Object.values(commands)) expect(command()).toBe(false);
    expect(f.options.onKeepFile).not.toHaveBeenCalled();
    expect(f.options.onNextFile).not.toHaveBeenCalled();
  });

  it("keeps explicit file-only error actions when empty geometry failed to paint", () => {
    const f = fixture();
    f.publish(f.options, []);
    f.unavailable();
    expect(f.actions.commands(f.port).accept()).toBe(true);
    expect(f.actions.commands(f.port).nextFile()).toBe(true);
    expect(f.actions.commands(f.port).nextChange()).toBe(false);
    expect(f.options.onKeepFile).toHaveBeenCalledOnce();
    expect(f.options.onKeepHunk).not.toHaveBeenCalled();
  });

  it("offers whole-file actions after explicit calculation failure, not stale coordinates", () => {
    const f = fixture();
    f.fail();
    expect(f.actions.unavailable).toBe(true);
    f.actions.commands(f.port).accept();
    expect(f.options.onKeepFile).toHaveBeenCalledOnce();
    expect(f.options.onKeepHunk).not.toHaveBeenCalled();
    f.edit();
    expect(f.actions.unavailable).toBe(false);
    f.actions.commands(f.port).accept();
    expect(f.options.onKeepFile).toHaveBeenCalledOnce();
  });

  it("uses sticky scope and refuses whole-review actions for a truncated walk", () => {
    const f = fixture();
    f.port.scope.current = "file";
    f.actions.commands(f.port).accept();
    f.port.scope.current = "all";
    f.actions.commands(f.port).reject();
    expect(f.options.onKeepFile).toHaveBeenCalledOnce();
    expect(f.options.onUndo).toHaveBeenCalledOnce();
    f.configure({ ...f.options, allActionsDisabled: true });
    expect(f.actions.commands(f.port).accept()).toBe(false);
    expect(f.options.onKeepAll).not.toHaveBeenCalled();
  });

  it.each([
    "fail",
    "unavailable",
  ] as const)("preserves the file-only command surface on %s", (reason) => {
    const f = fixture();
    f[reason]();
    const commands = f.actions.commands(f.port);
    for (const name of ["nextChange", "prevChange", "comment", "keepAll", "undo"] as const)
      expect(commands[name]()).toBe(false);
    for (const name of [
      "accept",
      "reject",
      "keepFile",
      "revertFile",
      "nextFile",
      "prevFile",
    ] as const)
      expect(commands[name]()).toBe(true);
    expect(f.options.onKeepHunk).not.toHaveBeenCalled();
    expect(f.options.onRevertHunk).not.toHaveBeenCalled();
    expect(f.options.onKeepAll).not.toHaveBeenCalled();
  });

  it.each([
    "clear",
    "dispose",
    "rebind",
    "geometry",
  ] as const)("does not follow a synchronous %s with stale navigation", (change) => {
    const f = fixture();
    const options = {
      ...f.options,
      onKeepHunk: () => {
        if (change === "clear") f.configure(undefined);
        else if (change === "dispose") f.actions.dispose();
        else if (change === "rebind") f.rebind();
        else f.publish(f.options, [{ ...f.hunk }]);
      },
    };
    f.configure(options);
    f.publish(options, [f.hunk]);
    expect(f.actions.commands(f.port).accept()).toBe(true);
    expect(f.options.onNextFile).not.toHaveBeenCalled();
    expect(f.port.revealLine).not.toHaveBeenCalled();
  });

  it("leaves composition keys alone and uses the presentation's actual comment line", () => {
    const f = fixture();
    f.actions.commands(f.port).comment();
    expect(f.port.openComment).toHaveBeenCalledWith(3, f.actions.options);
    f.compose();
    const commands = f.actions.commands(f.port);
    expect(commands.accept()).toBe(false);
    expect(commands.reject()).toBe(false);
    expect(commands.nextChange()).toBe(false);
    expect(commands.nextFile()).toBe(false);
  });

  it("does not run direct toolbar commands through an expired binding", () => {
    const f = fixture();
    const commands = f.actions.commands(f.port);
    f.rebind();
    expect(commands.keepFile()).toBe(false);
    expect(commands.nextFile()).toBe(false);
    expect(commands.comment()).toBe(false);
    expect(f.options.onKeepFile).not.toHaveBeenCalled();
  });
});
