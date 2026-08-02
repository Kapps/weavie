using Microsoft.AspNetCore.Http;

namespace Weavie.Hosting.Web;

/// <summary>The unauthenticated workspace entry rendered directly at <c>/index.html</c>.</summary>
internal static class WorkspaceConnectPage {
	public static async Task WriteAsync(HttpContext context, bool invalidToken) {
		context.Response.StatusCode = invalidToken
			? StatusCodes.Status401Unauthorized
			: StatusCodes.Status200OK;
		context.Response.ContentType = "text/html; charset=utf-8";
		context.Response.Headers.CacheControl = "no-store";
		context.Response.Headers["Referrer-Policy"] = "no-referrer";
		string error = invalidToken
			? "<p class=\"error\" role=\"alert\">That token was not accepted.</p>"
			: string.Empty;
		await context.Response.WriteAsync(
			$$"""
			<!doctype html>
			<html lang="en">
			<head>
			  <meta charset="utf-8">
			  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
			  <meta name="theme-color" content="#000000">
			  <link rel="icon" href="/weavie.png">
			  <title>Connect to Weavie</title>
			  <style>
			    :root { color-scheme: dark; font-family: system-ui, sans-serif; }
			    * { box-sizing: border-box; }
			    body { min-height: 100dvh; margin: 0; display: grid; place-items: center; padding: 20px;
			      background: #000; color: #cdd5dc; }
			    main { width: min(100%, 390px); padding: 28px; border: 1px solid #252a32; border-radius: 14px;
			      background: #101319; box-shadow: 0 18px 60px rgb(0 0 0 / 55%); }
			    header { display: flex; align-items: center; gap: 12px; margin-bottom: 24px; }
			    h1 { margin: 0; font-size: 20px; } p { color: #8b949e; line-height: 1.45; }
			    label { display: block; margin-bottom: 7px; font-size: 13px; font-weight: 600; }
			    input { width: 100%; min-height: 48px; padding: 0 12px; border: 1px solid #30363d;
			      border-radius: 9px; background: #000; color: #cdd5dc; font: 16px ui-monospace, monospace; }
			    input:focus { outline: 2px solid #54c6a4; outline-offset: 1px; }
			    button { width: 100%; min-height: 48px; margin-top: 12px; border: 1px solid #54c6a4;
			      border-radius: 9px; background: #14251f; color: #54c6a4; font-weight: 700; }
			    .error { color: #e07a7a; margin: 10px 0 0; }
			  </style>
			</head>
			<body>
			  <main>
			    <header><img src="/weavie.png" width="48" height="48" alt=""><h1>Connect to Weavie</h1></header>
			    <p>Enter the workspace token. This browser will remember it securely for future visits.</p>
			    <form method="post" action="/index.html">
			      <label for="token">Workspace token</label>
			      <input id="token" name="token" type="password" autocomplete="current-password" required autofocus>
			      <button type="submit">Connect</button>
			    </form>
			    {{error}}
			  </main>
			</body>
			</html>
			""",
			context.RequestAborted).ConfigureAwait(false);
	}
}
