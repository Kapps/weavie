import { describe, expect, it, vi } from "vitest";
import { reserveReviewLineWidth } from "./review-line-width";

function fixture() {
  const publish = vi.fn(function (this: { _maxLineWidth: number }, width: number) {
    this._maxLineWidth = width;
  });
  const layout = Object.assign(Object.create({ setMaxLineWidth: publish }), {
    _maxLineWidth: 120,
  }) as {
    _maxLineWidth: number;
    setMaxLineWidth(width: number): void;
  };
  return { layout, publish };
}

describe("owned prepared line width", () => {
  it("retains offscreen wrapped overflow across native visible-band resets", () => {
    const { layout, publish } = fixture();
    const held = reserveReviewLineWidth(layout, 1300);
    expect(layout._maxLineWidth).toBe(1300);
    layout.setMaxLineWidth(0);
    layout.setMaxLineWidth(300);
    expect(layout._maxLineWidth).toBe(1300);
    layout.setMaxLineWidth(1500);
    expect(layout._maxLineWidth).toBe(1500);
    held.clear();
    layout.setMaxLineWidth(250);
    expect(layout._maxLineWidth).toBe(250);
    held.dispose();
    held.dispose();
    expect(layout.setMaxLineWidth).toBe(publish);
    expect(Object.hasOwn(layout, "setMaxLineWidth")).toBe(false);
  });

  it("restores the exact original descriptor and native measurement on failed activation", () => {
    const { layout, publish } = fixture();
    Object.defineProperty(layout, "setMaxLineWidth", {
      value: publish,
      writable: true,
      configurable: true,
      enumerable: false,
    });
    const original = Object.getOwnPropertyDescriptor(layout, "setMaxLineWidth");
    const held = reserveReviewLineWidth(layout, 1300);
    held.dispose();
    expect(layout._maxLineWidth).toBe(120);
    expect(Object.getOwnPropertyDescriptor(layout, "setMaxLineWidth")).toEqual(original);
  });

  it("rejects duplicate owners and preserves a replacing owner on teardown", () => {
    const { layout } = fixture();
    const held = reserveReviewLineWidth(layout, 900);
    expect(() => reserveReviewLineWidth(layout, 1000)).toThrow("already has an owner");
    const hook = layout.setMaxLineWidth;
    const replacement = vi.fn((width: number) => hook.call(layout, width));
    layout.setMaxLineWidth = replacement;
    expect(() => held.dispose()).toThrow("was replaced");
    expect(layout.setMaxLineWidth).toBe(replacement);
    replacement(200);
    expect(layout._maxLineWidth).toBe(200);
  });

  it("rolls back installation when the initial publication fails", () => {
    const { layout, publish } = fixture();
    publish.mockImplementationOnce(() => {
      throw new Error("publication failed");
    });
    expect(() => reserveReviewLineWidth(layout, 800)).toThrow("publication failed");
    expect(layout.setMaxLineWidth).toBe(publish);
    expect(Object.hasOwn(layout, "setMaxLineWidth")).toBe(false);
    reserveReviewLineWidth(layout, 800).dispose();
  });
});
