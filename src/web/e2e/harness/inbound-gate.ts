import type { Page } from "@playwright/test";

/** Holds host→page WebSocket messages, as a slow link would, then delivers them in order. */
export interface InboundGate {
  hold(): void;
  release(): void;
}

const gates = new WeakMap<Page, InboundGate>();

/** A `preNavigate` hook that routes the page's WebSockets through an {@link InboundGate}. */
export const inboundGate = {
  async run(page: Page): Promise<void> {
    let held: (() => void)[] | null = null;
    gates.set(page, {
      hold: () => {
        held ??= [];
      },
      release: () => {
        const queued = held ?? [];
        held = null;
        for (const deliver of queued) deliver();
      },
    });
    await page.routeWebSocket("**/*", (socket) => {
      const server = socket.connectToServer();
      server.onMessage((data) => {
        if (held === null) socket.send(data);
        else held.push(() => socket.send(data));
      });
    });
  },
};

/** The gate `inboundGate` installed for `page`. */
export function gateFor(page: Page): InboundGate {
  const gate = gates.get(page);
  if (gate === undefined) throw new Error("inboundGate was not installed as preNavigate");
  return gate;
}
