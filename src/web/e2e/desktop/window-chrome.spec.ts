import { execFileSync } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:http";
import { expect, type TestInfo } from "@playwright/test";
import { test } from "./fixture";

// What the page reports about its bar from inside the shipped app, on each stage the window shows.
type Report = {
  stage: "welcome" | "workspace" | "pressed";
  chrome: WeavieWindowChrome | null;
  bar: { left: number; top: number; width: number; height: number } | null;
  start: string[];
  end: string[];
  frame: number;
};

const LABELS: Record<string, string> = {
  minimize: "Minimize",
  "maximize-toggle": "Maximize",
  close: "Close",
};

// Runs in each page: waits for the bar to render, reports it, then moves on from the welcome screen.
const driver = (workspace: string, origin: string) => `
  if (self === top) addEventListener('load', () => {
    const welcome = location.pathname === '/welcome.html';
    const report = () => {
      const chrome = (welcome ? window.__WEAVIE_WELCOME__ : window.__WEAVIE_SHELL__)?.chrome ?? null;
      const bar = document.querySelector(welcome ? '.welcome-bar' : '.titlebar');
      const labels = selector => [...document.querySelectorAll(selector)].map(b => b.getAttribute('aria-label'));
      const rect = bar?.getBoundingClientRect();
      return {
        stage: welcome ? 'welcome' : 'workspace', chrome,
        bar: rect ? {left: rect.left, top: rect.top, width: rect.width, height: rect.height} : null,
        start: labels(welcome ? '.welcome-bar > .tb-ctl:not(.welcome-bar-fill ~ .tb-ctl)' : '.titlebar > .tb-ctl'),
        end: labels(welcome ? '.welcome-bar-fill ~ .tb-ctl' : '.titlebar .tb-controls > .tb-ctl'),
        frame: outerHeight - innerHeight,
      };
    };
    const ready = () => welcome ? document.querySelector('.welcome') : document.querySelector('.titlebar .tb-omnibar');
    const post = () => fetch(${JSON.stringify(origin)}, {method: 'POST', body: JSON.stringify(report())});
    const poll = setInterval(() => {
      if (!ready()) return;
      clearInterval(poll);
      post().then(() => {
        if (!welcome) {
          // Tells the spec the page saw a bar press, so its drag waits out a busy start-up.
          document.querySelector('.titlebar').addEventListener('mousedown', () =>
            fetch(${JSON.stringify(origin)}, {method: 'POST', body: '{"stage":"pressed"}'}), true);
          return;
        }
        window.__weaviePostMessage(JSON.stringify({
          scope: 'host', session: null, kind: 'event', requestId: null, feature: 'window', name: 'menu', error: null,
          payload: {action: 'open-recent', path: ${JSON.stringify(workspace)}}
        }));
      });
    }, 50);
  });
`;

async function screenshot(info: TestInfo, name: string): Promise<void> {
  const path = info.outputPath(`${name}.png`);
  if (process.platform === "darwin") {
    // The app launches behind the runner's frontmost app; bring it forward so the shot shows it active.
    execFileSync("osascript", ["-e", 'tell application id "io.github.kapps.weavie" to activate']);
    execFileSync("sleep", ["1"]);
    execFileSync("screencapture", ["-x", path]);
  } else if (process.platform === "linux") {
    execFileSync("scrot", ["--overwrite", path]);
  } else {
    execFileSync("powershell", [
      "-NoProfile",
      "-Command",
      "Add-Type -AssemblyName System.Windows.Forms,System.Drawing;" +
        "$b=[System.Windows.Forms.SystemInformation]::VirtualScreen;" +
        "$i=New-Object System.Drawing.Bitmap $b.Width,$b.Height;" +
        "[System.Drawing.Graphics]::FromImage($i).CopyFromScreen($b.Left,$b.Top,0,0,$i.Size);" +
        `$i.Save('${path}')`,
    ]);
  }
  await info.attach(name, { path, contentType: "image/png" });
}

// Linux runs under a scriptable X server, so the drag there is real input on the real window.
const xdotool = (...args: string[]): string => execFileSync("xdotool", args, { encoding: "utf8" });

function windowOrigin(window: string): { x: number; y: number } {
  const geometry = Object.fromEntries(
    xdotool("getwindowgeometry", "--shell", window)
      .trim()
      .split("\n")
      .map((line) => line.split("=")),
  );
  return { x: Number(geometry.X), y: Number(geometry.Y) };
}

// Presses on empty bar space (right of the omnibar), waits until the page has seen the press and its report
// has had time to reach the host, then drags past the threshold step by step.
async function dragBar(
  window: string,
  bar: NonNullable<Report["bar"]>,
  pressed: Promise<unknown>,
): Promise<void> {
  const origin = windowOrigin(window);
  const x = Math.round(origin.x + bar.left + bar.width * 0.8);
  const y = Math.round(origin.y + bar.top + bar.height / 2);
  xdotool("mousemove", String(x), String(y));
  xdotool("mousedown", "1");
  await pressed;
  execFileSync("sleep", ["0.5"]);
  for (let step = 1; step <= 10; step++) {
    xdotool("mousemove", String(x + step * 12), String(y + step * 9));
    execFileSync("sleep", ["0.05"]);
  }
  xdotool("mouseup", "1");
}

test("the shipped app draws one title bar from its window's chrome", async ({ desktop }, info) => {
  const welcomeReport = Promise.withResolvers<Report>();
  const workspaceReport = Promise.withResolvers<Report>();
  const pressed = Promise.withResolvers<void>();
  const server = createServer((req, res) => {
    let body = "";
    req.on("data", (chunk) => {
      body += chunk;
    });
    req.on("end", () => {
      res.setHeader("Access-Control-Allow-Origin", "*");
      res.end();
      const report = JSON.parse(body) as Report;
      if (report.stage === "welcome") welcomeReport.resolve(report);
      else if (report.stage === "workspace") workspaceReport.resolve(report);
      else if (report.stage === "pressed") pressed.resolve();
    });
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const origin = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
  try {
    const app = await desktop((workspace) => driver(workspace, origin));
    const welcome = await Promise.race([welcomeReport.promise, app.exited]);
    const workspace = await Promise.race([workspaceReport.promise, app.exited]);
    await info.attach("reports.json", {
      body: JSON.stringify({ welcome, workspace }, null, 2),
      contentType: "application/json",
    });
    await new Promise((resolve) => setTimeout(resolve, 1000));
    await screenshot(info, "titlebar");

    const chrome = workspace.chrome!;
    expect(workspace.bar).not.toBeNull();
    expect(workspace.start).toEqual(chrome.controls.start.map((action) => LABELS[action]));
    expect(workspace.end).toEqual(chrome.controls.end.map((action) => LABELS[action]));
    // The page's bar is the window's only one: it sits at the very top of the page.
    expect(workspace.bar!.top).toBe(0);
    // No OS title bar above it. GTK keeps only a thin resize border where no compositor draws its shadow
    // (CI's X server), far short of a title bar. WKWebView's outer size is not the window's, so the Mac
    // test app measures the macOS bar against AppKit instead.
    if (process.platform === "linux") {
      expect(workspace.frame).toBeLessThan(20);
    }
    if (process.platform === "win32") {
      expect(chrome.caption).toBe("region");
      expect(welcome.chrome?.caption).toBe("none");
      expect(welcome.bar).toBeNull();
    } else if (process.platform === "darwin") {
      expect(chrome).toMatchObject({ menus: false, caption: "press" });
      expect(chrome.barHeight).toBeGreaterThan(28);
      expect(chrome.inset.start).toBeGreaterThan(60);
      expect(workspace.bar!.height).toBe(chrome.barHeight);
      expect(welcome.bar).toBeNull();
    } else {
      expect(chrome).toMatchObject({ menus: true, caption: "press", barHeight: null });
      expect(welcome.chrome?.controls).toEqual(chrome.controls);
      expect(welcome.bar).not.toBeNull();
      expect([...welcome.start, ...welcome.end]).toEqual([...workspace.start, ...workspace.end]);

      const window = xdotool("search", "--onlyvisible", "--class", "weavie").trim().split("\n")[0];
      const before = windowOrigin(window);
      await dragBar(window, workspace.bar!, pressed.promise);
      await expect.poll(() => windowOrigin(window)).not.toEqual(before);
      await screenshot(info, "after-drag");
    }
  } finally {
    server.closeAllConnections();
    server.close();
  }
});
