import { execFileSync } from "node:child_process";
import type { ServerResponse } from "node:http";
import { expect, type TestInfo } from "@playwright/test";
import { windowControlLabel } from "../../src/chrome/window-control-label";
import { localServer, test } from "./fixture";

// What the page reports about its bar from inside the shipped app, on each stage the window shows.
type Report = {
  stage: "welcome" | "workspace" | "pressed";
  chrome: WeavieWindowChrome | null;
  bar: { left: number; top: number; width: number; height: number } | null;
  start: string[];
  end: string[];
  frame: number;
  width: number;
  height: number;
};

// Runs in each page once the bar has painted: reports it, then does what the reply says. The welcome screen always
// reopens the workspace; a workspace closes back to welcome when told to.
const driver = (workspace: string, origin: string) => `
  if (self === top) addEventListener('load', () => {
    const welcome = location.pathname === '/welcome.html';
    const send = (name, payload) => window.__weaviePostMessage(JSON.stringify({
      scope: 'host', session: null, kind: 'event', requestId: null, feature: 'window', name, error: null, payload,
    }));
    const report = () => {
      const chrome = (welcome ? window.__WEAVIE_WELCOME__ : window.__WEAVIE_SHELL__)?.chrome ?? null;
      const bar = document.querySelector(welcome ? '.welcome-bar' : '.titlebar');
      const labels = selector => [...document.querySelectorAll(selector)].map(b => b.getAttribute('aria-label'));
      const rect = bar?.getBoundingClientRect();
      return {
        stage: welcome ? 'welcome' : 'workspace', chrome,
        bar: rect ? {left: rect.left, top: rect.top, width: rect.width, height: rect.height} : null,
        start: labels(welcome ? '.welcome-bar > .tb-ctl:not(.welcome-bar-fill ~ .tb-ctl)' : '.titlebar .tb-left > .tb-ctl'),
        end: labels(welcome ? '.welcome-bar-fill ~ .tb-ctl' : '.titlebar .tb-controls > .tb-ctl'),
        frame: outerHeight - innerHeight, width: innerWidth, height: innerHeight,
      };
    };
    const post = body => fetch(${JSON.stringify(origin)}, {method: 'POST', body: JSON.stringify(body)});
    const ready = () => welcome ? document.querySelector('.welcome') : document.querySelector('.titlebar .tb-omnibar');
    const poll = setInterval(() => {
      if (!ready()) return;
      clearInterval(poll);
      requestAnimationFrame(() => requestAnimationFrame(() => post(report()).then(r => r.text()).then(reply => {
        if (welcome) send('menu', {action: 'open-recent', path: ${JSON.stringify(workspace)}});
        else if (reply === 'close') send('menu', {action: 'close-window'});
      })));
      if (!welcome) document.querySelector('.titlebar').addEventListener('mousedown', () => post({stage: 'pressed'}), true);
    }, 50);
  });
`;

// Reports arrive in order per stage; a workspace report is answered when the spec says what the page does next.
function reportServer() {
  const queues = new Map<Report["stage"], { report: Report; reply: ServerResponse }[]>();
  const waiters = new Map<Report["stage"], () => void>();
  const listening = localServer((req, res) => {
    let body = "";
    req.on("data", (chunk) => {
      body += chunk;
    });
    req.on("end", () => {
      res.setHeader("Access-Control-Allow-Origin", "*");
      const report = JSON.parse(body) as Report;
      if (!["welcome", "workspace", "pressed"].includes(report.stage)) return void res.end();
      if (report.stage !== "workspace") res.end();
      const queue = queues.get(report.stage) ?? [];
      queues.set(report.stage, [...queue, { report, reply: res }]);
      waiters.get(report.stage)?.();
    });
  });
  const next = async (
    stage: Report["stage"],
  ): Promise<{ report: Report; reply: ServerResponse }> => {
    while ((queues.get(stage) ?? []).length === 0) {
      await new Promise<void>((resolve) => waiters.set(stage, resolve));
    }
    const [first, ...rest] = queues.get(stage)!;
    queues.set(stage, rest);
    return first!;
  };
  return { listening, next };
}

async function screenshot(info: TestInfo, name: string): Promise<void> {
  const path = info.outputPath(`${name}.png`);
  if (process.platform === "darwin") {
    // The app launches behind the runner's frontmost app; bring it forward so the shot shows it active.
    execFileSync("osascript", ["-e", 'tell application id "io.github.kapps.weavie" to activate']);
    const frontmost = () =>
      execFileSync("osascript", [
        "-e",
        'tell application "System Events" to get bundle identifier of first process whose frontmost is true',
      ])
        .toString()
        .trim();
    await expect.poll(frontmost).toBe("io.github.kapps.weavie");
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

// Presses empty bar space right of the omnibar and, once the page has seen the press, steps the pointer until the
// window follows: the host starts the move on the first motion past the threshold after the press report lands.
async function dragBar(window: string, bar: NonNullable<Report["bar"]>, pressed: Promise<unknown>) {
  const before = windowOrigin(window);
  const x = Math.round(before.x + bar.left + bar.width * 0.8);
  const y = Math.round(before.y + bar.top + bar.height / 2);
  xdotool("mousemove", String(x), String(y));
  xdotool("mousedown", "1");
  await pressed;
  try {
    for (let step = 1; step <= 200; step++) {
      xdotool("mousemove", String(x + (step % 20) * 6), String(y + (step % 20) * 4));
      const now = windowOrigin(window);
      if (now.x !== before.x || now.y !== before.y) return;
    }
    throw new Error("The window did not follow a drag on its title bar.");
  } finally {
    xdotool("mouseup", "1");
  }
}

const labels = (actions: WindowControlAction[]): string[] =>
  actions.map((action) => windowControlLabel(action, false));

test("the shipped app draws one title bar from its window's chrome", async ({ desktop }, info) => {
  const { listening, next } = reportServer();
  const { origin, close } = await listening;
  try {
    const app = await desktop((workspace) => driver(workspace, origin));
    const race = <T>(promise: Promise<T>) => Promise.race([promise, app.exited]);
    const welcome = (await race(next("welcome"))).report;
    const opened = await race(next("workspace"));
    const workspace = opened.report;
    await info.attach("reports.json", {
      body: JSON.stringify({ welcome, workspace }, null, 2),
      contentType: "application/json",
    });
    await screenshot(info, "titlebar");

    const chrome = workspace.chrome!;
    expect(workspace.bar).not.toBeNull();
    expect(workspace.start).toEqual(labels(chrome.controls.start));
    expect(workspace.end).toEqual(labels(chrome.controls.end));
    // The page's bar is the window's only one: it sits at the very top of the page.
    expect(workspace.bar!.top).toBe(0);
    if (process.platform === "win32") {
      expect(chrome.caption).toBe("region");
      expect(welcome.chrome?.caption).toBe("none");
      expect(welcome.bar).toBeNull();
    } else if (process.platform === "darwin") {
      // WKWebView's outer size is not the window's; the Mac test app measures the bar against AppKit.
      expect(chrome).toMatchObject({ menus: false, caption: "press" });
      expect(chrome.barHeight).toBeGreaterThan(28);
      expect(chrome.inset.start).toBeGreaterThan(60);
      expect(workspace.bar!.height).toBe(chrome.barHeight);
      expect(welcome.bar).toBeNull();
    } else {
      // GTK keeps only a thin resize border where no compositor draws a shadow, far short of a title bar.
      expect(workspace.frame).toBeLessThan(20);
      expect(chrome).toMatchObject({ menus: true, caption: "press", barHeight: null });
      expect(welcome.chrome?.controls).toEqual(chrome.controls);
      expect(welcome.bar).not.toBeNull();
      expect([...welcome.start, ...welcome.end]).toEqual([...workspace.start, ...workspace.end]);

      const window = xdotool("search", "--onlyvisible", "--class", "weavie").trim().split("\n")[0]!;
      await dragBar(window, workspace.bar!, race(next("pressed")));
      await screenshot(info, "after-drag");

      // Closing to welcome saves the window's size and reopening restores it: it must come back unchanged.
      opened.reply.end("close");
      await race(next("welcome"));
      const reopened = await race(next("workspace"));
      reopened.reply.end();
      expect({ width: reopened.report.width, height: reopened.report.height }).toEqual({
        width: workspace.width,
        height: workspace.height,
      });
    }
    if (!opened.reply.writableEnded) opened.reply.end();
  } finally {
    close();
  }
});
