using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;

namespace Weavie.Runner;

/// <summary>The authenticated, server-rendered runner status shown before opening the workspace.</summary>
internal static class RunnerStatusPage {
	public static Task WriteAcceptedAsync(HttpContext context) =>
		WriteAsync(
			context,
			"Opening Weavie",
			"Runner token accepted.",
			"Connecting…",
			"This browser will remember the token for future visits.",
			StatusCodes.Status200OK,
			refresh: true,
			openUrl: null);

	public static Task WriteWaitingAsync(
		HttpContext context,
		string workspace,
		string status,
		UpdateStatus update) =>
		WriteAsync(
			context,
			"Opening Weavie",
			workspace,
			$"Backend: {status}",
			UpdateLine(update),
			StatusCodes.Status200OK,
			refresh: true,
			openUrl: null);

	public static Task WriteConfigurationErrorAsync(HttpContext context, string detail) =>
		WriteAsync(
			context,
			"Runner configuration error",
			detail,
			"The workspace URL cannot receive this browser's cookie.",
			"Use the runner's canonical hostname or correct its TLS front configuration.",
			StatusCodes.Status500InternalServerError,
			refresh: false,
			openUrl: null);

	public static Task WriteAttentionAsync(
		HttpContext context,
		string workspace,
		string openUrl,
		UpdateStatus update) =>
		WriteAsync(
			context,
			"Weavie needs attention",
			workspace,
			"Backend: running",
			UpdateLine(update),
			StatusCodes.Status200OK,
			refresh: false,
			openUrl);

	private static async Task WriteAsync(
		HttpContext context,
		string title,
		string detail,
		string status,
		string metadata,
		int statusCode,
		bool refresh,
		string? openUrl) {
		context.Response.StatusCode = statusCode;
		context.Response.ContentType = "text/html; charset=utf-8";
		context.Response.Headers.CacheControl = "no-store";
		context.Response.Headers["Referrer-Policy"] = "no-referrer";
		context.Response.Headers.ContentSecurityPolicy =
			"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
		string refreshMeta = refresh
			? "<meta http-equiv=\"refresh\" content=\"1;url=/\">"
			: string.Empty;
		string open = openUrl is null
			? string.Empty
			: $"<a href=\"{HtmlEncoder.Default.Encode(openUrl)}\">Open Weavie</a>";
		await context.Response.WriteAsync(
			$$"""
			<!doctype html>
			<html lang="en">
			<head>
			  <meta charset="utf-8">
			  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
			  <meta name="theme-color" content="#000000">
			  {{refreshMeta}}
			  <title>{{HtmlEncoder.Default.Encode(title)}}</title>
			  <style>
			    :root { color-scheme: dark; font-family: system-ui, sans-serif; }
			    body { min-height: 100dvh; margin: 0; display: grid; place-items: center; padding: 20px;
			      background: #000; color: #cdd5dc; }
			    main { box-sizing: border-box; width: min(100%, 520px); padding: 28px; border: 1px solid #252a32;
			      border-radius: 14px; background: #101319; box-shadow: 0 18px 60px rgb(0 0 0 / 55%); }
			    h1 { margin: 0 0 10px; font-size: 20px; }
			    p { margin: 8px 0; color: #8b949e; line-height: 1.45; overflow-wrap: anywhere; }
			    .status { color: #54c6a4; font-weight: 650; }
			    .metadata { font-size: 13px; }
			    a { display: block; margin-top: 20px; padding: 12px; border: 1px solid #54c6a4;
			      border-radius: 9px; color: #54c6a4; font-weight: 700; text-align: center; text-decoration: none; }
			  </style>
			</head>
			<body>
			  <main>
			    <h1>{{HtmlEncoder.Default.Encode(title)}}</h1>
			    <p>{{HtmlEncoder.Default.Encode(detail)}}</p>
			    <p class="status">{{HtmlEncoder.Default.Encode(status)}}</p>
			    <p class="metadata">{{HtmlEncoder.Default.Encode(metadata)}}</p>
			    {{open}}
			  </main>
			</body>
			</html>
			""",
			context.RequestAborted).ConfigureAwait(false);
	}

	private static string UpdateLine(UpdateStatus update) {
		string line = $"Runner {update.RunnerBuild}" + (update.Enabled ? string.Empty : " · auto-update off");
		if (update.Enabled && update.Staged is { } staged) {
			line += $" · staged build {staged}";
		}

		if (update.Enabled && update.Phase != "idle") {
			line += $" · {update.Phase}" + (string.IsNullOrEmpty(update.Detail) ? string.Empty : $": {update.Detail}");
		}

		return update.RunnerBehind ? line + " · restart the runner to apply its update" : line;
	}
}
