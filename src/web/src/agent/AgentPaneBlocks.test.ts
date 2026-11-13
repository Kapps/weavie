import { describe, expect, it, vi } from "vitest";

vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

import { createRoot } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentPaneAccumulator } from "./AgentPaneAccumulator";
import { estimateEntrySize } from "./AgentPaneEstimate";
import { type AgentPaneModel, createAgentPaneModel } from "./AgentPaneModel";
import { chartHeight } from "./agent-block-layout";

const row = (fields: Partial<AgentPaneUpdate>): AgentPaneUpdate => ({
  type: "item-completed",
  providerId: "fake",
  threadId: "t",
  turnId: "1",
  ...fields,
});
const marker = row({
  itemId: "dashboard:rollout",
  itemType: "dashboard",
  summary: "Checkout rollout",
});
const member = (id: string, turnId: string, text?: string): AgentPaneUpdate =>
  row({
    turnId,
    itemId: `tool:${id}`,
    itemType: "block",
    category: "chart",
    summary: `${id} chart`,
    blockId: id,
    parentItemId: "dashboard:rollout",
    ...(text === undefined ? {} : { text }),
  });

function withModel(messages: readonly AgentPaneUpdate[], check: (model: AgentPaneModel) => void) {
  createRoot((dispose) => {
    const model = createAgentPaneModel({} as ClientSession, () => "all");
    let flush = () => {};
    new AgentPaneAccumulator((callback) => {
      flush = callback;
    }).ingestBatch("test", messages, model.publish);
    flush();
    check(model);
    dispose();
  });
}

describe("dashboards", () => {
  it("gathers every block that names a dashboard into its card, and lines a later turn's block", () => {
    withModel(
      [
        row({ type: "user-message", itemId: "u1", text: "watch it" }),
        member("p95", "1"),
        marker,
        member("err", "1"),
        row({ type: "user-message", turnId: "2", itemId: "u2", text: "add rps" }),
        member("rps", "2", "added to Checkout rollout"),
      ],
      (model) => {
        const entries = model.transcript().entries;
        expect(entries.map((entry) => entry.kind)).toEqual([
          "message",
          "dashboard",
          "message",
          "block-added",
        ]);
        const card = entries[1]!;
        expect(card).toMatchObject({ summary: "Checkout rollout", dashboardId: "rollout" });
        expect(card.asideEntries!.map((block) => block.block!.id)).toEqual(["p95", "err", "rps"]);
        expect(card.asideEntries!.map((block) => block.dashboardId)).toEqual([
          "rollout",
          "rollout",
          "rollout",
        ]);
        expect(entries[3]).toMatchObject({
          summary: "rps chart · added to Checkout rollout",
          dashboardId: "rollout",
        });
        expect(model.transcript().cardIndexes).toEqual([1]);
        expect(model.dashboard(null, "rollout")).toBe(card);
        expect(model.block(null, "rps")).toBe(card.asideEntries![2]);
        expect(model.cards()).toEqual([card]);
        expect(estimateEntrySize(card)).toBe(76 + 3 * chartHeight);
      },
    );
  });

  it("collapses a card off the strip, and shows only a later block's line before its dashboard row loads", () => {
    withModel(
      [
        member("p95", "1"),
        marker,
        { ...marker, type: "dashboard-collapsed", summary: null, status: "collapsed" },
      ],
      (model) => {
        const card = model.dashboard(null, "rollout")!;
        expect(card.asideCollapsed).toBe(true);
        expect(model.cards()).toEqual([]);
        expect(estimateEntrySize(card)).toBe(76);
      },
    );
    withModel([member("rps", "2", "added to Checkout rollout")], (model) => {
      expect(model.transcript().entries.map((entry) => entry.kind)).toEqual(["block-added"]);
      expect(model.block(null, "rps")).toBeUndefined();
    });
  });

  it("drops a retracted dashboard with its blocks", () => {
    withModel(
      [
        member("p95", "1"),
        marker,
        row({ type: "item-retracted", itemId: "tool:p95" }),
        row({ type: "item-retracted", itemId: "dashboard:rollout" }),
      ],
      (model) => {
        expect(model.transcript().entries).toEqual([]);
        expect(model.dashboard(null, "rollout")).toBeUndefined();
      },
    );
  });
});
