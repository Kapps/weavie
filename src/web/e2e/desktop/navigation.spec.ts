import { once } from "node:events";
import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { expect } from "@playwright/test";
import { type WebSocket, WebSocketServer } from "ws";
import { test } from "./fixture";

test.use({ desktopDocument: "bridge" });

for (const destination of ["foreign", "redirect"]) {
  test(`native policy cancels ${destination} top navigation`, async ({ desktop }, info) => {
    const connected = Promise.withResolvers<WebSocket>();
    const complete = Promise.withResolvers<string>();
    const server = createServer((req, res) => {
      const url = new URL(req.url!, "http://localhost");
      if (url.pathname === "/done") complete.resolve(url.searchParams.get("result")!);
      if (url.pathname === "/redirect")
        return void res.writeHead(302, { Location: "/foreign" }).end();
      res.setHeader("Content-Type", "text/html");
      res.end(`<script>fetch('/done?result=Foreign+document+executed')</script>`);
    });
    const sockets = new WebSocketServer({ server });
    sockets.on("connection", (socket) => {
      socket.on("message", (result) => complete.resolve(String(result)));
      socket.on("close", () => complete.resolve("Trusted document disconnected"));
      socket.on("error", (error) => complete.resolve(String(error)));
      connected.resolve(socket);
    });
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    const origin = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
    try {
      const app = await desktop(
        (workspace) => `
        addEventListener('load', () => {
          const send = message => window.__weaviePostMessage(JSON.stringify({scope:'host',session:null,error:null,...message}));
          if (location.pathname === '/welcome.html') {
            send({kind:'event',requestId:null,feature:'window',name:'menu',
              payload:{action:'open-recent',path:${JSON.stringify(workspace)}}}); return;
          }
          const control = new WebSocket(${JSON.stringify(origin.replace("http:", "ws:"))});
          const cancelled = new Promise(resolve => control.onmessage = resolve);
          control.onopen = () => (async () => {
            let receive, sequence = 0;
            window.__weavieReceive = raw => receive(JSON.parse(raw));
            const roundtrip = () => new Promise((resolve, reject) => {
              const requestId = 'navigation-check-' + (++sequence);
              receive = message => {
                if (message.requestId !== requestId) return;
                message.error ? reject(Error(JSON.stringify(message.error))) : resolve(message.payload);
              };
              send({kind:'request',requestId,feature:'settings',name:'get',payload:{key:'theme.mode'}});
            });
            const before = await roundtrip();
            location.href = ${JSON.stringify(`${origin}/${destination}`)};
            await cancelled;
            if (JSON.stringify(await roundtrip()) !== JSON.stringify(before)) throw Error('Trusted bridge state changed');
            control.send('pass');
          })().catch(error => control.send(String(error)));
        });
      `,
      );
      const control = await Promise.race([connected.promise, app.exited]);
      // This acknowledges the native denial decision, not completion of a browser load.
      await Promise.race([
        expect
          .poll(() => readFile(info.outputPath("desktop.log"), "utf8"), { timeout: info.timeout })
          .toContain("[native-bridge] Blocked main-frame navigation."),
        app.exited,
        complete.promise.then((result) => {
          throw new Error(`Document completed before native cancellation: ${result}`);
        }),
      ]);
      control.send("cancelled");
      expect(await Promise.race([complete.promise, app.exited])).toBe("pass");
    } finally {
      for (const socket of sockets.clients) socket.terminate();
      sockets.close();
      server.closeAllConnections();
      server.close();
    }
  });
}
