namespace Weavie.Runner;

/// <summary>
/// The runner's minimal session picker — a single self-contained HTML page that lists live sessions, creates
/// new ones, and links to each worker's URL. It drives the same control plane a native shell eventually will;
/// it exists so the runner is usable + demonstrable from a plain browser before that integration lands.
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
		  body { font-family: system-ui, sans-serif; margin: 0; background: #0d1117; color: #c9d1d9; }
		  header { display:flex; align-items:center; gap:1rem; padding:1rem 1.5rem; border-bottom:1px solid #21262d; }
		  h1 { font-size:1.1rem; margin:0; font-weight:600; }
		  main { padding:1.5rem; max-width:720px; }
		  button { font:inherit; background:#238636; color:#fff; border:0; border-radius:6px; padding:.5rem .9rem; cursor:pointer; }
		  button:hover { background:#2ea043; }
		  ul { list-style:none; padding:0; margin:1.25rem 0 0; }
		  li { display:flex; align-items:center; gap:.75rem; padding:.7rem .9rem; border:1px solid #21262d; border-radius:8px; margin-bottom:.5rem; }
		  li .branch { font-weight:600; flex:1; }
		  .status { font-size:.78rem; padding:.1rem .5rem; border-radius:999px; background:#21262d; color:#8b949e; }
		  .status.running { background:#193b2a; color:#3fb950; }
		  .status.failed { background:#3b1a1a; color:#f85149; }
		  a.open { color:#58a6ff; text-decoration:none; }
		  a.open:hover { text-decoration:underline; }
		  .del { background:#30363d; }
		  .del:hover { background:#48131a; }
		  .empty { color:#8b949e; }
		</style></head>
		<body>
		  <header><h1>Weavie runner</h1><button id="new">New session</button></header>
		  <main><ul id="list"><li class="empty">Loading…</li></ul></main>
		  <script>
		    const token = {{JsLiteral(token)}};
		    const headers = { "Authorization": "Bearer " + token, "Content-Type": "application/json" };
		    const list = document.getElementById("list");
		    async function refresh() {
		      const res = await fetch("/sessions", { headers });
		      if (!res.ok) { list.innerHTML = '<li class="empty">Failed to load (' + res.status + ').</li>'; return; }
		      const { sessions } = await res.json();
		      if (!sessions.length) { list.innerHTML = '<li class="empty">No sessions yet. Create one.</li>'; return; }
		      list.innerHTML = "";
		      for (const s of sessions) {
		        const li = document.createElement("li");
		        li.innerHTML = '<span class="branch"></span>'
		          + '<span class="status ' + s.status + '">' + s.status + '</span>'
		          + '<a class="open" target="_blank">Open</a>'
		          + '<button class="del">Delete</button>';
		        li.querySelector(".branch").textContent = s.branch;
		        li.querySelector(".open").href = s.url;
		        li.querySelector(".del").onclick = async () => {
		          await fetch("/sessions/" + s.id, { method: "DELETE", headers });
		          refresh();
		        };
		        list.appendChild(li);
		      }
		    }
		    document.getElementById("new").onclick = async () => {
		      await fetch("/sessions", { method: "POST", headers, body: "{}" });
		      refresh();
		    };
		    refresh();
		    setInterval(refresh, 3000);
		  </script>
		</body></html>
		""";

	private static string JsLiteral(string value) =>
		"\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
