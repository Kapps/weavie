using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Weavie.Core.Git;
using Weavie.Core.Worktrees;

namespace Weavie.Runner;

/// <summary>
/// Maps the runner's auth'd control plane: list / create / destroy sessions, plus a minimal picker page. The
/// token (runner token) is required on every JSON route via <c>Authorization: Bearer</c> or a <c>?token=</c>
/// query. Each session's <c>url</c> is built against the request's own host, so reaching the runner at
/// <c>box:9000</c> yields session URLs at <c>box:&lt;port&gt;</c> with no public-host config.
/// </summary>
internal static class ControlApi {
	public static void Map(WebApplication app, SessionManager sessions, RunnerOptions options) {
		app.MapGet("/", (HttpContext ctx) => {
			if (!Authorized(ctx, options)) {
				return Results.Content(PickerPage.Unauthorized(), "text/html; charset=utf-8");
			}

			return Results.Content(PickerPage.Html(QueryToken(ctx) ?? string.Empty), "text/html; charset=utf-8");
		});

		app.MapGet("/sessions", (HttpContext ctx) => {
			if (!Authorized(ctx, options)) {
				return Results.Unauthorized();
			}

			string host = HostOf(ctx);
			var payload = sessions.Sessions.Select(s => Describe(s, host));
			return Results.Json(new { sessions = payload });
		});

		app.MapPost("/sessions", async (HttpContext ctx) => {
			if (!Authorized(ctx, options)) {
				return Results.Unauthorized();
			}

			CreateRequest? body = null;
			if (ctx.Request.ContentLength is > 0) {
				try {
					body = await ctx.Request.ReadFromJsonAsync<CreateRequest>().ConfigureAwait(false);
				} catch (JsonException) {
					return Results.BadRequest(new { error = "Malformed JSON body." });
				}
			}

			try {
				var session = await sessions.CreateAsync(body?.Branch, body?.Base, ctx.RequestAborted).ConfigureAwait(false);
				return Results.Json(Describe(session, HostOf(ctx)));
			} catch (InvalidOperationException ex) {
				return Results.Conflict(new { error = ex.Message });
			} catch (GitException ex) {
				return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
			}
		});

		app.MapDelete("/sessions/{id}", async (HttpContext ctx, string id) => {
			if (!Authorized(ctx, options)) {
				return Results.Unauthorized();
			}

			bool removed = await sessions.DestroyAsync(id, ctx.RequestAborted).ConfigureAwait(false);
			return removed ? Results.NoContent() : Results.NotFound();
		});
	}

	private static object Describe(RemoteSession session, string host) => new {
		id = session.Id,
		branch = session.Branch,
		status = session.Status,
		url = session.PageUrl(host),
	};

	private static bool Authorized(HttpContext ctx, RunnerOptions options) {
		string? presented = QueryToken(ctx);
		if (presented is null) {
			string? header = ctx.Request.Headers.Authorization.ToString();
			if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
				presented = header["Bearer ".Length..].Trim();
			}
		}

		return presented is not null && CryptographicEquals(presented, options.RunnerToken);
	}

	private static string? QueryToken(HttpContext ctx) =>
		ctx.Request.Query.TryGetValue("token", out var t) && !string.IsNullOrEmpty(t) ? t.ToString() : null;

	private static string HostOf(HttpContext ctx) {
		// The host the client used to reach the runner, minus any port — workers live on their own ports.
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

	private sealed record CreateRequest(string? Branch, string? Base);
}
