import { describe, expect, it, vi } from "vitest";
import { type ScratchSaveReply, saveStableScratch } from "./scratch-save-guard";

const saved: ScratchSaveReply = {
  scratchPath: "/scratch/Untitled-1",
  status: "saved",
  savedPath: "/workspace/saved.txt",
  error: null,
};

describe("saveStableScratch", () => {
  it("returns the host result when the flushed working-copy version remains stable", async () => {
    const flush = vi.fn(() => Promise.resolve());

    await expect(
      saveStableScratch(
        () => 7,
        flush,
        () => Promise.resolve(saved),
      ),
    ).resolves.toEqual({ status: "complete", reply: saved });
    expect(flush).toHaveBeenCalledOnce();
  });

  it("restores the scratch and refuses conversion when it changes during the host save", async () => {
    let version = 7;
    let finishSave: (reply: ScratchSaveReply) => void = () => {};
    const pendingSave = new Promise<ScratchSaveReply>((resolve) => {
      finishSave = resolve;
    });
    const flush = vi.fn(() => Promise.resolve());
    const attempt = saveStableScratch(
      () => version,
      flush,
      () => pendingSave,
    );
    await vi.waitFor(() => expect(flush).toHaveBeenCalledOnce());

    version = 8;
    finishSave(saved);

    await expect(attempt).resolves.toEqual({
      status: "changed",
      savedPath: "/workspace/saved.txt",
    });
    expect(flush).toHaveBeenCalledTimes(2);
  });
});
