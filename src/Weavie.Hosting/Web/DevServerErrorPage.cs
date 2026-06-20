// Debug-only failure page — Release has no dev server, so the whole type is compiled out to stay
// dead-code-free under the zero-warning gate.
#if DEBUG
using System.Net;
using System.Text;

namespace Weavie.Hosting.Web;

/// <summary>
/// Builds the loud "Vite dev server failed to start" page a host renders into its WebView instead of silently
/// serving a possibly-stale bundle (a fallback this project forbids). It shows the real failure reason and the
/// tail of Vite's output, and offers two explicit choices via the <c>weavie-dev://</c> links the host
/// intercepts (<see cref="DevWebBringUp.RetryUrl"/> / <see cref="DevWebBringUp.BundleUrl"/>).
/// </summary>
internal static class DevServerErrorPage {
	public static string Render(DevServerFailureInfo failure) {
		ArgumentNullException.ThrowIfNull(failure);

		string summary = WebUtility.HtmlEncode(failure.Summary);
		var tail = new StringBuilder();
		foreach (string line in failure.OutputTail) {
			tail.Append(WebUtility.HtmlEncode(line)).Append('\n');
		}

		string output = tail.Length == 0 ? "(no output captured)" : tail.ToString();
		string retryUrl = DevWebBringUp.RetryUrl;
		string bundleUrl = DevWebBringUp.BundleUrl;

		// $$"""…""": single braces are literal (handy for CSS); only {{…}} interpolates.
		return $$"""
		<!doctype html>
		<html lang="en">
		<head>
		<meta charset="utf-8">
		<meta name="color-scheme" content="dark">
		<title>Vite dev server failed</title>
		<style>
		  html, body { height: 100%; margin: 0; }
		  body {
		    background: #000; color: #d6d6d6;
		    font: 14px/1.55 -apple-system, "Segoe UI", system-ui, sans-serif;
		    display: flex; align-items: center; justify-content: center;
		    padding: 2rem; box-sizing: border-box;
		  }
		  .card { max-width: 760px; width: 100%; }
		  h1 { font-size: 1.3rem; margin: 0 0 .35rem; color: #f2f2f2; font-weight: 600; }
		  .sub { color: #ffb74d; margin: 0 0 1.25rem; font-weight: 500; }
		  .reason { color: #c8c8c8; margin: 0 0 1.5rem; }
		  .label { text-transform: uppercase; letter-spacing: .08em; font-size: 11px; color: #6b6b6b; margin: 0 0 .4rem; }
		  .fix { font-family: ui-monospace, "Cascadia Code", Consolas, monospace; background: #0c0c0c;
		    border: 1px solid #1c1c1c; border-radius: 6px; padding: .6rem .8rem; color: #6fdc66; margin: 0 0 1.5rem; }
		  pre.output { font-family: ui-monospace, "Cascadia Code", Consolas, monospace; background: #0a0a0a;
		    border: 1px solid #1c1c1c; border-radius: 6px; padding: .8rem; margin: 0 0 1.75rem;
		    max-height: 40vh; overflow: auto; white-space: pre-wrap; word-break: break-word;
		    color: #d18a8a; font-size: 12.5px; }
		  .actions { display: flex; gap: .75rem; }
		  .btn { display: inline-block; text-decoration: none; cursor: pointer; padding: .55rem 1.15rem;
		    border-radius: 6px; font-size: 13px; font-weight: 500; border: 1px solid #2a2a2a;
		    color: #d6d6d6; background: #141414; }
		  .btn:hover { border-color: #3a3a3a; background: #1a1a1a; }
		  .btn.primary { background: #14302a; border-color: #2f6f5f; color: #6fdc66; }
		  .btn.primary:hover { background: #1a3d35; }
		</style>
		</head>
		<body>
		  <div class="card">
		    <h1>Vite dev server failed to start</h1>
		    <p class="sub">Not serving live source — the bundled build may be stale and was not loaded.</p>
		    <p class="reason">{{summary}}</p>
		    <p class="label">Common fix</p>
		    <div class="fix">cd src/web &amp;&amp; corepack pnpm install --force</div>
		    <p class="label">Vite output</p>
		    <pre class="output">{{output}}</pre>
		    <div class="actions">
		      <a class="btn primary" href="{{retryUrl}}">Retry dev server</a>
		      <a class="btn" href="{{bundleUrl}}">Load stale bundle anyway</a>
		    </div>
		  </div>
		</body>
		</html>
		""";
	}
}
#endif
