import type { Virtualizer } from "@tanstack/solid-virtual";
import { expect, it, vi } from "vitest";

vi.mock("@tanstack/solid-virtual", () => ({
  elementScroll: (
    offset: number,
    _options: unknown,
    instance: { scrollElement: { scrollTop: number } },
  ) => {
    instance.scrollElement.scrollTop = offset;
  },
}));

const { scrollVirtualElement } = await import("./virtual-scroll");

for (const adjustment of [-20, 20]) {
  it(`preserves the live scroll target across a ${adjustment}px geometry change`, () => {
    let maximum = 100;
    let position = 90;
    const scroller = {
      get scrollTop(): number {
        return position;
      },
      set scrollTop(value: number) {
        position = Math.max(0, Math.min(value, maximum));
      },
    };
    const instance = { scrollElement: scroller } as unknown as Virtualizer<
      HTMLElement,
      HTMLElement
    >;

    const target = scrollVirtualElement(80, { adjustments: adjustment }, instance, () => {
      maximum += adjustment;
      scroller.scrollTop = position;
    });

    expect(target).toBe(90 + adjustment);
    expect(scroller.scrollTop).toBe(target);
  });
}
