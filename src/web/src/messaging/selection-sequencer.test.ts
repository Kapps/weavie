import { describe, expect, it } from "vitest";
import { SelectionSequencer } from "./selection-sequencer";

function harness(): {
  applied: string[];
  sequencer: SelectionSequencer<string>;
} {
  const applied: string[] = [];
  return {
    applied,
    sequencer: new SelectionSequencer((value) => {
      applied.push(value);
      return true;
    }),
  };
}

describe("SelectionSequencer", () => {
  it("lets the newest explicit selection intent commit once", () => {
    const { applied, sequencer } = harness();
    const stale = sequencer.beginIntent();
    const current = sequencer.beginIntent();

    expect(stale("stale")).toBe(false);
    expect(current("current")).toBe(true);
    expect(current("again")).toBe(false);
    expect(applied).toEqual(["current"]);
  });

  it("settles activating candidates in invocation order regardless of response order", () => {
    const { applied, sequencer } = harness();
    const first = sequencer.beginCandidate();
    const second = sequencer.beginCandidate();

    expect(second("second")).toBe(true);
    expect(first("first")).toBe(false);
    expect(applied).toEqual(["second"]);
  });

  it("allows a newer activating candidate to replace one that completed first", () => {
    const { applied, sequencer } = harness();
    const first = sequencer.beginCandidate();
    const second = sequencer.beginCandidate();

    expect(first("first")).toBe(true);
    expect(second("second")).toBe(true);
    expect(applied).toEqual(["first", "second"]);
  });

  it("does not let an unused background candidate invalidate a selection", () => {
    const { applied, sequencer } = harness();
    const selection = sequencer.beginIntent();
    sequencer.beginCandidate();

    expect(selection("selected")).toBe(true);
    expect(applied).toEqual(["selected"]);
  });

  it("rejects a pending activation after a newer explicit selection", () => {
    const { applied, sequencer } = harness();
    const activation = sequencer.beginCandidate();
    const selection = sequencer.beginIntent();

    expect(selection("clicked")).toBe(true);
    expect(activation("late")).toBe(false);
    expect(applied).toEqual(["clicked"]);
  });
});
