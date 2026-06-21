namespace Weavie.Runner;

/// <summary>
/// The runner's minimal landing page: ensures the workspace backend is up and offers a single "Open Weavie"
/// link into it, so the runner is usable from a plain browser. Once connected, the user works in the real app
/// against the remote backend, including New Session (worktrees on the remote box via the shared HostCore).
/// </summary>
internal static class PickerPage {
	public static string Unauthorized() =>
		"<!doctype html><meta charset=utf-8><body style=\"font-family:system-ui;padding:2rem;background:#0d1117;color:#c9d1d9\">"
		+ "<h1>Weavie runner</h1><p>Unauthorized. Append <code>?token=&lt;runner-token&gt;</code> "
		+ "(printed in the runner's console at startup) to this URL.</p></body>";

	public static string Html(string token) =>
		$$"""
		<!doctype html>
		<html><head><meta charset="utf-8"><title>Weavie runner</title>
		<style>
		  :root { color-scheme: dark; }
		  body { font-family: system-ui, sans-serif; margin: 0; background: #0d1117; color: #c9d1d9;
		         display: grid; place-items: center; height: 100vh; }
		  .card { border: 1px solid #21262d; border-radius: 12px; padding: 2rem 2.25rem; max-width: 520px; text-align: center; }
		  h1 { font-size: 1.15rem; margin: 0 0 .25rem; font-weight: 600; }
		  .ws { color: #8b949e; font-size: .85rem; word-break: break-all; margin-bottom: 1.25rem; }
		  a.open { display: inline-block; background: #238636; color: #fff; text-decoration: none;
		           border-radius: 8px; padding: .6rem 1.1rem; font-weight: 600; }
		  a.open[aria-disabled="true"] { background: #30363d; color: #8b949e; pointer-events: none; }
		  a.open:hover { background: #2ea043; }
		  .status { margin-top: 1rem; font-size: .8rem; color: #8b949e; }
		  .status.running { color: #3fb950; }
		  .status.failed { color: #f85149; }
		</style></head>
		<body>
		  <div class="card">
		    <h1>Weavie runner</h1>
		    <div class="ws" id="ws">resolving workspace…</div>
		    <a class="open" id="open" aria-disabled="true">Open Weavie</a>
		    <div class="status" id="status">starting backend…</div>
		  </div>
		  <script>
		    const token = {{JsLiteral(token)}};
		    const headers = { "Authorization": "Bearer " + token };
		    const open = document.getElementById("open");
		    const statusEl = document.getElementById("status");
		    async function refresh() {
		      try {
		        const res = await fetch("/backend", { headers });
		        if (!res.ok) { statusEl.textContent = "error (" + res.status + ")"; return; }
		        const b = await res.json();
		        document.getElementById("ws").textContent = b.workspace;
		        open.href = b.url;
		        open.setAttribute("aria-disabled", b.status === "running" ? "false" : "true");
		        statusEl.className = "status " + b.status;
		        statusEl.textContent = "backend: " + b.status;
		      } catch (e) { statusEl.textContent = "unreachable"; }
		    }
		    refresh();
		    setInterval(refresh, 1500);
		  </script>
		</body></html>
		""";

	private static string JsLiteral(string value) =>
		"\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
