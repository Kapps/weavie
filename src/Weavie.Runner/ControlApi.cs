using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Weavie.Runner;

/// <summary>
/// The runner's auth'd control plane. A single default-deny middleware (registered before any endpoint)
/// requires the runner token on every request, so new endpoints are gated automatically. Each backend
/// <c>url</c> is built against the request's own host. See docs/specs/remote-sessions.md.
/// </summary>
internal static class ControlApi {
	public static void Map(WebApplication app, BackendManager backends, RunnerOptions options, ITlsFront front, Func<UpdateStatus> updateStatus) {
		ArgumentNullException.ThrowIfNull(updateStatus);
		// The one auth gate, registered first so it covers every endpoint. Unauthorized → 401 with a
		// constant hint body, never derived from the request.
		app.Use(async (context, next) => {
			if (Authorized(context, options)) {
				await next().ConfigureAwait(false);
				return;
			}

			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			context.Response.ContentType = "text/html; charset=utf-8";
			await context.Response.WriteAsync(PickerPage.Unauthorized()).ConfigureAwait(false);
		});

		app.MapGet("/", (HttpContext ctx) =>
			Results.Content(PickerPage.Html(QueryToken(ctx) ?? string.Empty), "text/html; charset=utf-8"));

		// Ensure the workspace backend is running and return its connect URL + status (+ the updater's state,
		// which the picker page renders — runner staleness and a rollback must be visible where the user is).
		app.MapGet("/backend", (HttpContext ctx) => {
			var backend = backends.Ensure();
			return Results.Json(new {
				url = front.WorkerPageUrl(HostOf(ctx), backend),
				status = backend.Status,
				workspace = backend.WorkspaceRoot,
				update = updateStatus(),
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
