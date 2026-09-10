import { randomUUID } from "node:crypto";
import { once } from "node:events";
import { createServer } from "node:http";
import { expect } from "@playwright/test";
import { test } from "./fixture";

test("only the app can use the native bridge, across welcome, previews and reload", async ({
  desktop,
}) => {
  const nonce = randomUUID();
  const acks = new Set<string>();
  const complete = Promise.withResolvers<string>();
  let workspace = "";
  const attack = () => `
    const report = path => fetch(location.origin + path, {mode:'no-cors'});
    window.__weavieReceive = () => report('/leak');
    if (location.pathname === '/opaque' && self.origin !== 'null') report('/leak');
    if (['__weaviePostMessage','__WEAVIE_WELCOME__','__WEAVIE_RESOURCE_BASE__'].some(key => key in window)) report('/leak');
    try { top.__weaviePostMessage('{}'); report('/leak'); } catch {}
    const body = JSON.stringify({scope:'host',session:null,kind:'request',requestId:'attack',feature:'commands',name:'invoke',payload:{id:'weavie.font.increase',args:null},error:null});
    const menu = JSON.stringify({scope:'host',session:null,kind:'event',requestId:null,feature:'window',name:'menu',payload:{action:'open-recent',path:${JSON.stringify(workspace)}},error:null});
    for (const value of [body, menu, '0'.repeat(64) + ':' + body, '0'.repeat(64) + ':' + menu]) {
      try { webkit.messageHandlers.weavie.postMessage(value); } catch {}
      try { chrome.webview.postMessage(value); } catch {}
    }
    if (location.pathname === '/top' && (window.chrome?.webview || window.webkit?.messageHandlers?.weavie)) report('/leak');
    report(location.pathname === '/top' ? '/external' : '/ack' + location.pathname);
    if (location.pathname === '/preview') document.body.insertAdjacentHTML('beforeend', '<iframe sandbox="allow-scripts" src="/opaque"></iframe>');
    addEventListener('message', () => { try { top.location = location.origin + '/top'; } catch {} window.open('/top'); report('/ack/interactive'); });
  `;
  const server = createServer((req, res) => {
    const url = new URL(req.url!, "http://localhost");
    res.setHeader("Access-Control-Allow-Origin", "*");
    if (url.pathname === "/done" && url.searchParams.get("nonce") === nonce)
      complete.resolve(url.searchParams.get("result")!);
    if (url.pathname.startsWith("/ack/")) acks.add(url.pathname);
    if (url.pathname === "/leak") complete.resolve("Untrusted document received bridge access");
    if (url.pathname === "/state") return void res.end(JSON.stringify([...acks]));
    if (url.pathname === "/redirect")
      return void res.writeHead(302, { Location: "/preview" }).end();
    if (url.pathname === "/opaque")
      res.setHeader("Content-Security-Policy", "sandbox allow-scripts");
    res.setHeader("Content-Type", "text/html");
    res.end(
      `<h1>Untrusted preview</h1><input placeholder="Preview still works"><script>${attack()}</script>`,
    );
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const origin = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
  try {
    const app = await desktop((path) => {
      workspace = path;
      return `
      (async () => {
        const origin = ${JSON.stringify(origin)}, nonce = ${JSON.stringify(nonce)};
        const wait = async condition => { while (!await condition()) await new Promise(resolve => setTimeout(resolve, 25)); };
        const query = selector => document.querySelector(selector);
        const command = async label => {
          const input = query('.tb-omnibar-input'); input.focus(); input.click(); input.value = '>' + label;
          input.dispatchEvent(new Event('input', {bubbles:true}));
          const row = () => [...document.querySelectorAll('.tb-omnibar-row')].find(row => row.querySelector('.tb-row-leaf')?.textContent === label);
          await wait(row); row().dispatchEvent(new MouseEvent('mousedown', {bubbles:true,button:0}));
        };
        const frame = url => { const f = document.createElement('iframe'); f.src = url; document.body.append(f); return f; };
        if (location.pathname === '/welcome.html') {
          await wait(() => document.querySelectorAll('.welcome-row').length === 2);
          frame(origin + '/welcome');
          await wait(async () => (await (await fetch(origin + '/state')).json()).includes('/ack/welcome'));
          document.querySelectorAll('.welcome-row')[1].click(); return;
        }
        await wait(() => query('.tb-omnibar-input') && !query('#splash'));
        if (window.__WEAVIE_BRIDGE_WS__ !== undefined) throw Error('Expected native transport');
        window.__weavieReceive = ((receive) => raw => { if (JSON.parse(raw).requestId === 'attack') fetch(origin + '/leak'); receive(raw); })(window.__weavieReceive);
        const font = () => getComputedStyle(document.documentElement).getPropertyValue('--font-content-size').trim();
        if (!sessionStorage.getItem('bridge-reloaded')) {
          await command('Open URL…'); await wait(() => query('.url-prompt-input'));
          const input = query('.url-prompt-input'); input.value = origin + '/redirect'; input.dispatchEvent(new Event('input',{bubbles:true})); input.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));
          await wait(() => query('.editor-web:not([hidden]) iframe'));
          await wait(async () => (await (await fetch(origin + '/state')).json()).includes('/ack/opaque'));
          query('.editor-web:not([hidden]) iframe').contentWindow.postMessage('interact','*');
          await wait(async () => (await (await fetch(origin + '/state')).json()).includes('/ack/interactive'));
          const child = frame(location.href.replace('://', '://user@')); await new Promise(resolve => setTimeout(resolve, 100));
          try { if (child.contentWindow.__weaviePostMessage) throw Error('App subframe has a sender'); } catch (error) { if (error.name !== 'SecurityError') throw error; }
          for (const url of [origin + '/top', origin + '/redirect', 'data:text/html,untrusted']) { location.href = url; await new Promise(resolve => setTimeout(resolve, 100)); }
          await command('Increase Font Size'); await wait(() => font() === '17px');
          sessionStorage.setItem('bridge-reloaded','yes'); location.reload(); return;
        }
        await command('Increase Font Size'); await wait(() => font() === '18px');
        await fetch(origin + '/done?nonce=' + nonce + '&result=pass');
      })().catch(error => fetch(${JSON.stringify(origin)} + '/done?nonce=' + ${JSON.stringify(nonce)} + '&result=' + encodeURIComponent(String(error))));
    `;
    });
    expect(await Promise.race([complete.promise, app.exited])).toBe("pass");
    expect(acks).toEqual(
      new Set(["/ack/welcome", "/ack/preview", "/ack/opaque", "/ack/interactive"]),
    );
  } finally {
    server.closeAllConnections();
    server.close();
  }
});
