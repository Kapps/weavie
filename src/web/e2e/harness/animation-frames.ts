import type { Page } from "@playwright/test";

/** Holds rendering callbacks so tests can order input ahead of stale focus work. */
export async function withHeldAnimationFrames(
  page: Page,
  run: (release: () => Promise<void>) => Promise<void>,
): Promise<void> {
  const frames = await page.evaluateHandle(() => {
    const request = window.requestAnimationFrame.bind(window);
    const cancel = window.cancelAnimationFrame.bind(window);
    const pending = new Map<number, FrameRequestCallback>();
    window.requestAnimationFrame = (callback) => {
      const handle = request(() => {});
      pending.set(handle, callback);
      return handle;
    };
    window.cancelAnimationFrame = (handle) => {
      pending.delete(handle);
      cancel(handle);
    };
    return {
      release: async () => {
        window.requestAnimationFrame = request;
        window.cancelAnimationFrame = cancel;
        for (const callback of pending.values()) request(callback);
        pending.clear();
        await new Promise<void>((resolve) => request(() => resolve()));
      },
    };
  });
  const release = (): Promise<void> => frames.evaluate((held) => held.release());
  try {
    await run(release);
  } finally {
    await release();
    await frames.dispose();
  }
}
