export function waitForWorkspace(proc, timeoutMs) {
  return new Promise((resolve, reject) => {
    let output = "";
    const finish = () => {
      clearTimeout(timer);
      proc.stdout.off("data", onData);
      proc.off("exit", onExit);
    };
    const onData = (chunk) => {
      output += chunk.toString("utf8");
      const pageUrl = output.match(/open\s+(http:\/\/\S+)/)?.[1];
      const token = output.match(/\[weavie-headless\] token ([^\s]+)/)?.[1];
      if (pageUrl && token) {
        finish();
        resolve({ pageUrl, token });
      }
    };
    const onExit = (code) => {
      finish();
      reject(new Error(`host exited early with code ${code}`));
    };
    const timer = setTimeout(() => {
      finish();
      reject(new Error("host did not report its page and token in time"));
    }, timeoutMs);
    proc.stdout.on("data", onData);
    proc.on("exit", onExit);
  });
}

export async function openWorkspace(page, workspace) {
  const connected = await page.request.post(workspace.pageUrl, {
    form: { token: workspace.token },
    maxRedirects: 0,
  });
  if (connected.status() !== 302) {
    throw new Error(`workspace authentication failed (${connected.status()})`);
  }
  await page.goto(workspace.pageUrl, { waitUntil: "load" });
}
