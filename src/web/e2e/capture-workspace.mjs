import { waitForOutputLines } from "./harness/process-output.mjs";

export async function waitForWorkspace(proc, timeoutMs) {
  const [pageUrl, token] = await waitForOutputLines(
    proc,
    [/\[weavie-headless\] open\s+(http:\/\/\S+)/, /\[weavie-headless\] token ([^\s]+)/],
    timeoutMs,
  );
  return { pageUrl, token };
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
