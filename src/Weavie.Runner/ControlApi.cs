using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Weavie.Runner;

/// <summary>
/// Maps the runner's auth'd control plane. Since worktree sessions are now created inside the worker by the
/// shared HostCore, the control plane is small: it provisions/auths the one multi-session workspace backend and
/// hands the client its URL+token. The token (runner token) is required on every route via
/// <c>Authorization: Bearer</c> or a <c>?token=</c> query. The backend <c>url</c> is built against the request's
/// own host, so reaching the runner at <c>box:9000</c> yields a backend URL at <c>box:&lt;port&gt;</c>.
/// </summary>
internal static class ControlApi {
	public static void Map(WebApplication app, BackendManager backends, RunnerOptions options) {
		app.MapGet("/", (HttpContext ctx) =>
			Authorized(ctx, options)
				? Results.Content(PickerPage.Html(QueryToken(ctx) ?? string.Empty), "text/html; charset=utf-8")
				: Results.Content(PickerPage.Unauthorized(), "text/html; charset=utf-8"));

		// Ensure the workspace backend is running and return its connect URL + status. The client opens the URL;
		// inside it, New Session creates worktree sessions on the remote box via the shared HostCore.
		app.MapGet("/backend", (HttpContext ctx) => {
			if (!Authorized(ctx, options)) {
				return Results.Unauthorized();
			}

			var backend = backends.Ensure();
			return Results.Json(new {
				url = backend.PageUrl(HostOf(ctx)),
				status = backend.Status,
				workspace = backend.WorkspaceRoot,
			});
		});
	}

	private static bool Authorized(HttpContext ctx, RunnerOptions options) {
		string? presented = QueryToken(ctx);
		if (presented is null) {
			string header = ctx.Request.Headers.Authorization.ToString();
			if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
				presented = header["Bearer ".Length..].Trim();
			}
		}

		return presented is not null && CryptographicEquals(presented, options.RunnerToken);
	}

	private static string? QueryToken(HttpContext ctx) =>
		ctx.Request.Query.TryGetValue("token", out var t) && !string.IsNullOrEmpty(t) ? t.ToString() : null;

	private static string HostOf(HttpContext ctx) {
		// The host the client used to reach the runner, minus any port — the worker lives on its own port.
		string host = ctx.Request.Host.Host;
		return string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
	}

	private static bool CryptographicEquals(string a, string b) {
		if (a.Length != b.Length) {
			return false;
		}

		int diff = 0;
		for (int i = 0; i < a.Length; i++) {
			diff |= a[i] ^ b[i];
		}

		return diff == 0;
	}
}
