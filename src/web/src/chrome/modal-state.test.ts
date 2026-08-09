import { afterEach, describe, expect, it, vi } from "vitest";
import { evaluateWhen } from "../commands/context";
import { modalActive, onModalOpened, requestModal } from "./modal-state";

const cancellations: Array<() => void> = [];

afterEach(() => {
  for (const cancel of cancellations.splice(0).reverse()) {
    cancel();
  }
});

describe("modal state", () => {
  it("shows one modal at a time and preserves queued requests", () => {
    const first = vi.fn();
    const second = vi.fn();
    const cancelFirst = requestModal(first);
    const cancelSecond = requestModal(second);
    cancellations.push(cancelFirst, cancelSecond);

    expect(first).toHaveBeenCalledOnce();
    expect(second).not.toHaveBeenCalled();
    expect(modalActive()).toBe(true);
    expect(evaluateWhen("modalOpen")).toBe(true);

    cancelFirst();
    expect(second).toHaveBeenCalledOnce();
    expect(evaluateWhen("modalOpen")).toBe(true);

    cancelSecond();
    expect(modalActive()).toBe(false);
    expect(evaluateWhen("modalOpen")).toBe(false);
  });

  it("notifies transient overlays as each queued modal becomes active", () => {
    const opened = vi.fn();
    const unsubscribe = onModalOpened(opened);
    const cancelFirst = requestModal(() => {});
    const cancelSecond = requestModal(() => {});
    cancellations.push(cancelFirst, cancelSecond);

    expect(opened).toHaveBeenCalledOnce();
    cancelFirst();
    expect(opened).toHaveBeenCalledTimes(2);

    unsubscribe();
  });
});
