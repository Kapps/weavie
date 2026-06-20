using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Weavie.Runner;

/// <summary>
/// The runner's auth'd control plane. Auth is enforced by a SINGLE default-deny middleware
/// (<see cref="Map"/> registers it before any endpoint): every request must present the runner token
/// (<c>Authorization: Bearer</c> or <c>?token=</c>) — so a new endpoint is gated automatically, with no
/// per-route check to forget. CORS preflight (OPTIONS) is handled upstream and never reaches here. Each
/// session's <c>url</c> is built against the request's own host, so reaching the runner at <c>box:9000</c>
/// yields a backend URL at <c>box:&lt;port&gt;</c>.
/// </summary>
internal static class ControlApi {
	public static void Map(WebApplication app, BackendManager backends, RunnerOptions options) {
		// The one and only auth gate. Registered before the endpoints, so it runs first and covers them all
		// (and anything added later). Unauthorized → 401; the landing page returns a friendly hint body.
		app.Use(async (context, next) => {
			if (Authorized(context, options)) {
				await next().ConfigureAwait(false);
				return;
			}

			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			if (context.Request.Path == "/") {
				context.Response.ContentType = "text/html; charset=utf-8";
				await context.Response.WriteAsync(PickerPage.Unauthorized()).ConfigureAwait(false);
			}
		});

		app.MapGet("/", (HttpContext ctx) =>
			Results.Content(PickerPage.Html(QueryToken(ctx) ?? string.Empty), "text/html; charset=utf-8"));

		// Ensure the workspace backend is running and return its connect URL + status. The client opens the URL;
		// inside it, New Session creates worktree sessions on the remote box via the shared HostCore.
		app.MapGet("/backend", (HttpContext ctx) => {
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
