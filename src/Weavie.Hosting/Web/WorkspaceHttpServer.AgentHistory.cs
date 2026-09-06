using Microsoft.AspNetCore.Http;
using Weavie.Hosting.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Web;

public sealed partial class WorkspaceHttpServer {
	private async Task ServeAgentHistoryAsync(HttpContext context) {
		if (_authentication.TransportTokenMatches(context)) {
			context.Response.Headers.AccessControlAllowOrigin = "*";
		} else if (context.Request.Headers.Origin.Count > 0
			&& !_authentication.CookieWebSocketOriginMatches(context)) {
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}

		string slot = context.Request.Query["slot"].ToString();
		string incarnation = context.Request.Query["incarnation"].ToString();
		if (string.IsNullOrEmpty(slot) || string.IsNullOrEmpty(incarnation)) {
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		var request = new AgentPaneHistoryRequest(
			ReadRevision(context, "knownGeneration"),
			ReadRevision(context, "knownRevision"));
		context.Response.ContentType = "application/x-ndjson";
		context.Response.Headers.CacheControl = "no-store";
		context.Response.Headers.XContentTypeOptions = "nosniff";
		context.Response.Headers["Referrer-Policy"] = "no-referrer";
		try {
			if (!await _core.WriteAgentHistoryAsync(
				new SessionAddress(slot, incarnation),
				request,
				context.Response.Body,
				context.RequestAborted).ConfigureAwait(false)) {
				context.Response.StatusCode = StatusCodes.Status404NotFound;
			}
		} catch (ArgumentException) when (!context.Response.HasStarted) {
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
		}
	}

	private static long? ReadRevision(HttpContext context, string name) {
		string value = context.Request.Query[name].ToString();
		if (value.Length == 0) {
			return null;
		}
		return long.TryParse(value, out long revision) && revision >= 0
			? revision
			: throw new BadHttpRequestException("Invalid transcript revision.");
	}
}
