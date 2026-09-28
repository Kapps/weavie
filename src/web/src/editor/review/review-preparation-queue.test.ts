import { afterEach, describe, expect, it, vi } from "vitest";
import { createReviewPreparationQueue } from "./review-preparation-queue";

afterEach(() => vi.useRealTimers());

describe("review preparation scheduling", () => {
  it("paints a ready file while another file is waiting for its resources", async () => {
    vi.useFakeTimers();
    const queue = createReviewPreparationQueue();
    const held = Promise.withResolvers<string>();
    const painted: string[] = [];
    const first = queue(
      () => held.promise,
      (value) => painted.push(value),
    );
    const second = queue(
      async () => "ready",
      (value) => painted.push(value),
    );
    await vi.runAllTimersAsync();
    await second;
    expect(painted).toEqual(["ready"]);
    held.resolve("released");
    await vi.runAllTimersAsync();
    await first;
    expect(painted).toEqual(["ready", "released"]);
  });

  it("surfaces a paint failure without poisoning subsequent files", async () => {
    vi.useFakeTimers();
    const queue = createReviewPreparationQueue();
    const failure = new Error("projection failed");
    const first = queue(
      async () => 1,
      () => {
        throw failure;
      },
    );
    const rejected = expect(first).rejects.toBe(failure);
    const paint = vi.fn();
    const second = queue(async () => 2, paint);
    await vi.runAllTimersAsync();
    await rejected;
    await second;
    expect(paint).toHaveBeenCalledExactlyOnceWith(2);
  });
});
