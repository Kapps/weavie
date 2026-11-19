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

describe("carried edits when storage is full", () => {
  it("stays in memory for as long as the page lives", () => {
    const setItem = window.sessionStorage.setItem;
    window.sessionStorage.setItem = () => {
      throw new Error("quota");
    };
    storeCarriedEdits(incarnation("old"), { "b.ts": "large" });
    window.sessionStorage.setItem = setItem;
    expect(carriedEdits(incarnation("new"))).toEqual({ "b.ts": "large" });
  });
});

describe("carried edits when storage refuses to remove them", () => {
  beforeEach(() => window.sessionStorage.clear());

  it("are forgotten for the departed slot all the same, and removed at the next cleanup", () => {
    storeCarriedEdits(incarnation("old"), { "c.ts": "text" });
    const removeItem = window.sessionStorage.removeItem;
    window.sessionStorage.removeItem = () => {
      throw new Error("denied");
    };
    expect(() => clearSlotDrafts("local", "slot")).not.toThrow();
    window.sessionStorage.removeItem = removeItem;
    expect(carriedEdits(incarnation("new"))).toEqual({});

    clearSlotDrafts("local", "other");
    expect(stored()).toEqual([]);
  });

  it("keep what a slot in use again writes, and nothing from before it departed", () => {
    storeCarriedEdits(incarnation("old"), { "c.ts": "text" });
    const removeItem = window.sessionStorage.removeItem;
    window.sessionStorage.removeItem = () => {
      throw new Error("denied");
    };
    clearSlotDrafts("local", "slot");
    window.sessionStorage.removeItem = removeItem;

    storeCarriedEdits(incarnation("reused"), { "d.ts": "new" });
    clearSlotDrafts("local", "other");
    expect(stored()).toEqual(["weavie-session-draft:editor-edits:local:slot"]);
    expect(carriedEdits(incarnation("next"))).toEqual({ "d.ts": "new" });
  });
});

const stored = (): string[] =>
  Object.keys(window.sessionStorage).filter((key) => key.startsWith("weavie-session-draft"));

describe("carried edits of hosts whose ids end alike", () => {
  beforeEach(() => window.sessionStorage.clear());

  it("are forgotten only for the exact host and slot", () => {
    const remote = {
      connection: { id: "remote:local" },
      address: { slot: "slot", incarnation: "r" },
    } as ClientSession;
    storeCarriedEdits(remote, { "e.ts": "remote" });
    clearSlotDrafts("local", "slot");
    expect(carriedEdits(remote)).toEqual({ "e.ts": "remote" });
    expect(stored()).toEqual(["weavie-session-draft:editor-edits:remote:local:slot"]);
  });
});
