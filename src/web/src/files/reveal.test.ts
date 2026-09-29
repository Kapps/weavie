import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { openFilesIn, ownFileNavigation, revealFileIn } from "./reveal";

it("file reveals belong to the exact session and retired owners cannot remove their replacement", async () => {
  const original = { address: { slot: "same-slot", incarnation: "1" } } as ClientSession;
  const replacement = { address: { slot: "same-slot", incarnation: "2" } } as ClientSession;
  const first = vi.fn(async () => {});
  const second = vi.fn(async () => {});
  const batch = vi.fn(async () => {});
  const retire = ownFileNavigation(original, { reveal: first, openFiles: batch });
  const stopReplacement = ownFileNavigation(replacement, { reveal: second, openFiles: batch });
  expect(() => ownFileNavigation(original, { reveal: first, openFiles: batch })).toThrow(
    "already owns",
  );
  await revealFileIn(original, "/first.ts", undefined, true);
  expect(first).toHaveBeenCalledExactlyOnceWith("/first.ts", undefined, true);
  expect(second).not.toHaveBeenCalled();
  retire();
  expect(() => revealFileIn(original, "/stale.ts", 3, false)).toThrow("no longer owns");
  const rebound = vi.fn(async () => {});
  const stopRebound = ownFileNavigation(original, { reveal: rebound, openFiles: batch });
  retire();
  await revealFileIn(original, "/rebound.ts", 7, false);
  expect(rebound).toHaveBeenCalledExactlyOnceWith("/rebound.ts", 7, false);
  await revealFileIn(replacement, "/replacement.ts", 9, false);
  expect(second).toHaveBeenCalledExactlyOnceWith("/replacement.ts", 9, false);
  await openFilesIn(replacement, ["/one.ts", "/two.ts"]);
  expect(batch).toHaveBeenCalledExactlyOnceWith(["/one.ts", "/two.ts"]);
  stopRebound();
  stopReplacement();
});
