import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Locator, type Page } from "@playwright/test";
import { MockHost, type MockLiveSession, mockSession } from "../mock-host";
import { test as base } from "./network-fixtures";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "dist");

export const iPhone = {
  viewport: { width: 390, height: 844 },
  hasTouch: true,
  userAgent:
    "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
};

export interface AgentPane {
  body: Locator;
  deltas: number;
  host: MockHost;
  page: Page;
  session: MockLiveSession;
}

/** Connects the page to a mock host with one ACP session per name; the pane shows the first. */
export type OpenPanes = (transcripts: Record<string, unknown[]>) => Promise<AgentPane>;

export const test = base.extend<{ openPanes: OpenPanes }>({
  openPanes: async ({ page }, use) => {
    const hosts: MockHost[] = [];
    await use(async (transcripts) => {
      if (!existsSync(join(distDir, "index.html"))) {
        throw new Error(
          `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
        );
      }
      const sessions = Object.keys(transcripts).map((name) => mockSession(name, name, "acp"));
      const host = await MockHost.start({ distDir, sessions });
      hosts.push(host);
      for (const session of sessions) {
        const messages = transcripts[session.id]!;
        host.setAgentHistory(session.address, { generation: 1, batchSize: 5000, messages });
      }
      await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
      await host.waitUntilConnected();
      return { body: page.locator(".agent-body"), deltas: 0, host, page, session: sessions[0]! };
    });
    await Promise.all(hosts.map((host) => host.close()));
  },
});

/** `count` turns of prompt, tool call, and answer of varying length. */
export function agentTurns(count: number): Record<string, unknown>[] {
  return Array.from({ length: count }, (_, turn) => [
    {
      providerId: "acp",
      type: "user-message",
      turnId: `h${turn}`,
      itemId: `u-${turn}`,
      text: `Prompt ${turn}`,
    },
    {
      providerId: "acp",
      type: "item-completed",
      turnId: `h${turn}`,
      itemId: `tool-${turn}`,
      itemType: "commandExecution",
      status: "completed",
      title: `grep -rn "thing" src/${turn}`,
      text: `src/file${turn}.ts: matched`,
    },
    {
      providerId: "acp",
      type: "item-completed",
      turnId: `h${turn}`,
      itemId: `a-${turn}`,
      itemType: "agentMessage",
      status: "completed",
      text: `### Answer ${turn}\n\n${"A paragraph that wraps in the pane. ".repeat((turn % 5) + 2)}[source ${turn}](https://example.com/${turn})`,
    },
  ]).flat();
}

/** Publishes the next small delta of a live reply, the way real agents deliver one; returns its index. */
export function publishDelta(pane: AgentPane, itemId: string): number {
  const index = pane.deltas++;
  pane.host.publishAgentPane(pane.session.address, {
    providerId: "acp",
    type: "agent-message-delta",
    turnId: "live",
    itemId,
    itemType: "agentMessage",
    text: index % 6 === 5 ? `delta ${index}.\n\n` : `delta ${index} `,
  });
  return index;
}

/** Wheels up by `distance`, waits to be `past` that far from the bottom, and for the smoothing to settle. */
export async function wheelUp(pane: AgentPane, distance: number, past: number): Promise<void> {
  await pane.body.hover();
  await pane.page.mouse.wheel(0, -distance);
  await expect
    .poll(() => pane.body.evaluate((element) => -element.scrollTop))
    .toBeGreaterThan(past);
  await pane.body.evaluate(untilStill);
}

/** Resolves once the scroll position has held for two frames, so a wheel animation has finished. */
export async function untilStill(element: HTMLElement): Promise<void> {
  let previous = Number.NaN;
  for (let still = 0; still < 2; ) {
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    still = element.scrollTop === previous ? still + 1 : 0;
    previous = element.scrollTop;
  }
}

/** The topmost visible row and its offset from the viewport top. */
export function readAnchor(element: HTMLElement): { id: string; top: number } {
  const viewport = element.getBoundingClientRect();
  const row = [...element.querySelectorAll<HTMLElement>(".agent-virtual-row")].find((candidate) => {
    const bounds = candidate.getBoundingClientRect();
    return bounds.bottom > viewport.top && bounds.top < viewport.bottom;
  });
  if (row === undefined) throw new Error("No visible transcript row");
  return { id: row.dataset.transcriptEntry!, top: row.getBoundingClientRect().top - viewport.top };
}

/** How far down an anchored row has moved. */
export function anchorMovement(element: HTMLElement, anchor: { id: string; top: number }): number {
  const row = element.querySelector<HTMLElement>(
    `.agent-virtual-row[data-transcript-entry="${CSS.escape(anchor.id)}"]`,
  );
  if (row === null) throw new Error(`Anchored row ${anchor.id} is no longer mounted`);
  return row.getBoundingClientRect().top - element.getBoundingClientRect().top - anchor.top;
}
