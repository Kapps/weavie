import { randomUUID } from "node:crypto";
import { once } from "node:events";
import { createServer } from "node:http";
import { expect } from "@playwright/test";
import { test } from "./fixture";

test("native bridge isolates foreign frames across welcome, workspace and reload", async ({
  desktop,
}, info) => {
  const nonce = randomUUID();
  const complete = Promise.withResolvers<string>();
  const stages: string[] = [];
  const attack = `
    let leaked = false;
    const leak = () => { leaked = true; parent.postMessage({kind:'leak'}, '*'); };
    window.__weavieReceive = leak;
    try { Object.defineProperty(window, '__weavieDeliver', {value:leak}); } catch {}
    window.chrome?.webview?.addEventListener('message', leak);
    if (['__weaviePostMessage','__WEAVIE_WELCOME__','__WEAVIE_RESOURCE_BASE__'].some(key => key in window)) leak();
    try { top.__weaviePostMessage; leak(); } catch {}
    addEventListener('message', event => {
      if (event.data === 'inspect') parent.postMessage({kind:'inspected',leaked}, '*');
      if (event.data !== 'attack') return;
      const body = JSON.stringify({scope:'host',session:null,kind:'request',requestId:'attack',
        feature:'settings',name:'set',payload:{key:'theme.mode',value:'light'},error:null});
      for (const value of [body, '0'.repeat(64) + ':' + body]) {
        try { webkit.messageHandlers.weavie.postMessage(value); } catch {}
        try { chrome.webview.postMessage(value); } catch {}
      }
      parent.postMessage({kind:'attacked',opaque:self.origin === 'null'}, '*');
    });
    parent.postMessage({kind:'ready'}, '*');
  `;
  const server = createServer((req, res) => {
    const url = new URL(req.url!, "http://localhost");
    res.setHeader("Access-Control-Allow-Origin", "*");
    if (url.pathname === "/report" && url.searchParams.get("nonce") === nonce) {
      const result = url.searchParams.get("result")!;
      stages.push(result);
      if (result === "pass" || result.startsWith("FAIL:")) complete.resolve(result);
      return void res.end();
    }
    if (url.pathname === "/redirect")
      return void res.writeHead(302, { Location: "/foreign" }).end();
    if (url.pathname === "/opaque")
      res.setHeader("Content-Security-Policy", "sandbox allow-scripts");
    res.setHeader("Content-Type", "text/html");
    res.end(`<h2>Untrusted ${url.pathname} frame</h2><script>${attack}</script>`);
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const origin = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
  try {
    const app = await desktop(
      (workspace) => `
      (async () => {
        const origin = ${JSON.stringify(origin)}, nonce = ${JSON.stringify(nonce)};
        const report = result => {
          document.querySelector('#results').textContent += result + '\\n';
          return fetch(origin + '/report?nonce=' + nonce + '&result=' + encodeURIComponent(result));
        };
        const fail = error => report('FAIL: ' + error);
        addEventListener('error', event => fail(event.message));
        addEventListener('unhandledrejection', event => fail(event.reason));
        const check = (condition, message) => { if (!condition) throw Error(message); };
        check(self === top, 'The app document must be the top frame');
        check(typeof window.__weaviePostMessage === 'function', 'Native startup capability missing');
        check(window.__WEAVIE_BRIDGE_WS__ === undefined, 'Expected native transport');
        const pending = new Map();
        window.__weavieReceive = raw => {
          const message = JSON.parse(raw);
          if (message.requestId === 'attack') fail('Untrusted request received a response');
          if (message.kind !== 'response') return;
          const response = pending.get(message.requestId);
          if (!response) return;
          pending.delete(message.requestId);
          message.error ? response.reject(Error(JSON.stringify(message.error))) : response.resolve(message.payload);
        };
        let sequence = 0;
        const send = message => window.__weaviePostMessage(JSON.stringify({scope:'host',session:null,error:null,...message}));
        const request = (name, payload) => new Promise((resolve, reject) => {
          const requestId = 'trusted-' + (++sequence);
          pending.set(requestId, {resolve, reject});
          send({kind:'request',requestId,feature:'settings',name,payload});
        });
        const page = location.pathname === '/welcome.html' ? 'welcome' :
          sessionStorage.getItem('bridge-reloaded') ? 'reloaded workspace' : 'workspace';
        check((page === 'welcome') === !!window.__WEAVIE_WELCOME__, 'Welcome startup escaped its document');
        await request('set', {key:'theme.mode',value:'dark'});
        check((await request('get', {key:'theme.mode'})).value === 'dark', 'Trusted setting write failed');
        await report(page + ': trusted roundtrip');
        const frames = new Map();
        addEventListener('message', event => {
          const frame = frames.get(event.source);
          if (!frame) return;
          if (event.data.kind === 'leak') { fail('Untrusted frame received bridge authority or a reply'); return; }
          if (event.data.kind === 'ready') frame.ready();
          if (event.data.kind === 'attacked') frame.attacked(event.data.opaque);
          if (event.data.kind === 'inspected') frame.inspected(event.data.leaked);
        });
        for (const path of ['/redirect', '/opaque']) {
          const frame = document.createElement('iframe');
          let ready, attacked, inspected;
          const loaded = new Promise(resolve => ready = resolve);
          const attempted = new Promise(resolve => attacked = resolve);
          const isolated = new Promise(resolve => inspected = resolve);
          document.body.append(frame);
          frames.set(frame.contentWindow, {ready, attacked, inspected, isolated});
          frame.src = origin + path;
          await loaded;
          frame.contentWindow.postMessage('attack', '*');
          check(await attempted === (path === '/opaque'), 'Frame origin did not match its sandbox');
          check((await request('get', {key:'theme.mode'})).value === 'dark', 'Untrusted frame changed host settings');
          await report(page + ': ' + path + ' attack rejected');
        }
        // A second trusted roundtrip runs with both hostile reply listeners still installed.
        check((await request('get', {key:'theme.mode'})).value === 'dark', 'Reply isolation failed');
        for (const [frame, state] of frames) {
          frame.postMessage('inspect', '*');
          check(!await state.isolated, 'Reply reached an untrusted frame');
        }
        await report(page + ': replies isolated');
        if (page === 'welcome') {
          check(window.__WEAVIE_WELCOME__.recents.includes(${JSON.stringify(workspace)}), 'Welcome recents missing');
          send({kind:'event',requestId:null,feature:'window',name:'menu',
            payload:{action:'open-recent',path:${JSON.stringify(workspace)}}});
        } else if (page === 'workspace') {
          sessionStorage.setItem('bridge-reloaded', 'yes'); location.reload();
        } else {
          await report('pass');
        }
      })().catch(error => fetch(${JSON.stringify(origin)} + '/report?nonce=' + ${JSON.stringify(nonce)} + '&result=' + encodeURIComponent('FAIL: ' + error)));
    `,
    );
    expect(await Promise.race([complete.promise, app.exited])).toBe("pass");
    expect(stages).toEqual([
      ...["welcome", "workspace", "reloaded workspace"].flatMap((page) => [
        `${page}: trusted roundtrip`,
        `${page}: /redirect attack rejected`,
        `${page}: /opaque attack rejected`,
        `${page}: replies isolated`,
      ]),
      "pass",
    ]);
  } finally {
    await info.attach("bridge-stages.json", {
      body: JSON.stringify(stages, null, 2),
      contentType: "application/json",
    });
    server.closeAllConnections();
    server.close();
  }
});
