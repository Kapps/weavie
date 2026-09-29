import { Sequencer } from "@codingame/monaco-vscode-api/vscode/vs/base/common/async";

/** Resource readiness is per-file; only synchronous projection and paint share a queue. */
export function createReviewPreparationQueue() {
  const queue = new Sequencer();
  return async <T>(load: () => Promise<T>, paint: (value: T) => void): Promise<void> => {
    const value = await load();
    await queue.queue(async () => {
      try {
        paint(value);
      } finally {
        await new Promise<void>((resolve) => setTimeout(resolve, 0));
      }
    });
  };
}

export type ReviewPreparationQueue = ReturnType<typeof createReviewPreparationQueue>;
