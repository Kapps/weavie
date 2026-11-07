import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { clearSlotDrafts } from "../messaging/session-drafts";
import { carriedEdits, storeCarriedEdits } from "./carried-edits";

const storage = Object.create({
  getItem(this: Record<string, string>, key: string) {
    return this[key] ?? null;
  },
  setItem(this: Record<string, string>, key: string, value: string) {
    this[key] = value;
  },
  removeItem(this: Record<string, string>, key: string) {
    delete this[key];
  },
  clear(this: Record<string, string>) {
    for (const key of Object.keys(this)) {
      delete this[key];
    }
  },
});
vi.stubGlobal("window", { sessionStorage: storage });

const incarnation = (id: string): ClientSession =>
  ({ connection: { id: "local" }, address: { slot: "slot", incarnation: id } }) as ClientSession;

describe("carried edits", () => {
  beforeEach(() => window.sessionStorage.clear());

  it("follows the slot into its next incarnation", () => {
    storeCarriedEdits(incarnation("old"), { "a.ts": "text" });
    expect(carriedEdits(incarnation("new"))).toEqual({ "a.ts": "text" });
  });

  it("is gone once stored empty", () => {
    storeCarriedEdits(incarnation("old"), { "a.ts": "text" });
    storeCarriedEdits(incarnation("old"), {});
    expect(carriedEdits(incarnation("new"))).toEqual({});
  });

  it("is forgotten only for the departed slot", () => {
    storeCarriedEdits(incarnation("old"), { "a.ts": "text" });
    clearSlotDrafts("local", "other");
    expect(carriedEdits(incarnation("new"))).toEqual({ "a.ts": "text" });
    clearSlotDrafts("local", "slot");
    expect(carriedEdits(incarnation("new"))).toEqual({});
  });
});
