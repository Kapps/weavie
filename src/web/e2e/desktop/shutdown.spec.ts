import { once } from "node:events";
import { createServer } from "node:http";
import { expect } from "@playwright/test";
import { test } from "./fixture";

for (const stage of ["welcome", "workspace"]) {
  test(`desktop shuts down cleanly from ${stage}`, async ({ desktop }) => {
    const ready = Promise.withResolvers<string>();
    const server = createServer((req, res) => {
      res.setHeader("Access-Control-Allow-Origin", "*");
      res.end();
      ready.resolve(req.url!);
    });
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    const origin = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
    try {
      const app = await desktop(
        (workspace) => `
        if (self === top) {
          if (location.pathname === '/welcome.html' && ${JSON.stringify(stage)} === 'workspace') {
            window.__weaviePostMessage(JSON.stringify({
              scope: 'host', session: null, kind: 'event', requestId: null,
              feature: 'window', name: 'menu', error: null,
              payload: {action: 'open-recent', path: ${JSON.stringify(workspace)}}
            }));
          } else fetch(${JSON.stringify(origin)} + location.pathname);
        }
      `,
      );
      expect(await Promise.race([ready.promise, app.exited])).toBe(
        stage === "welcome" ? "/welcome.html" : "/index.html",
      );
    } finally {
      server.closeAllConnections();
      server.close();
    }
  });
}
