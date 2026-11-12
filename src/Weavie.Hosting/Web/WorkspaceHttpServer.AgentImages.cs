using Microsoft.AspNetCore.Http;
using Weavie.Core.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Web;

public sealed partial class WorkspaceHttpServer {
	// A refusal answers 422 with its reason as plain text, which the pane shows in the image's place.
	private async Task ServeAgentImageAsync(HttpContext context) {
		string url = context.Request.Query["url"].ToString();
		if (SessionResource(context, "private, no-cache") is not { } address) return;
		if (url.Length == 0) {
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		AgentImage? image;
		try {
			image = await _core.FetchAgentImageAsync(address, url, context.RequestAborted)
				.ConfigureAwait(false);
		} catch (AgentImageRefusedException refused) {
			context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
			context.Response.ContentType = "text/plain; charset=utf-8";
			await context.Response.WriteAsync(refused.Message, context.RequestAborted).ConfigureAwait(false);
			return;
		}
		if (image is null) {
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}
		context.Response.ContentType = image.ContentType;
		await context.Response.Body.WriteAsync(image.Bytes, context.RequestAborted).ConfigureAwait(false);
	}

	// The session a resource request names by slot and incarnation, with the headers every session resource sends;
	// null once the request has been answered (wrong origin, or no address).
	private SessionAddress? SessionResource(HttpContext context, string cacheControl) {
		if (!AllowSessionResourceOrigin(context)) return null;
		string slot = context.Request.Query["slot"].ToString();
		string incarnation = context.Request.Query["incarnation"].ToString();
		if (string.IsNullOrEmpty(slot) || string.IsNullOrEmpty(incarnation)) {
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return null;
		}
		context.Response.Headers.CacheControl = cacheControl;
		context.Response.Headers.XContentTypeOptions = "nosniff";
		context.Response.Headers["Referrer-Policy"] = "no-referrer";
		return new SessionAddress(slot, incarnation);
	}

	// A token-authenticated request may come from another origin (a remote client); a cookie-only one may not.
	private bool AllowSessionResourceOrigin(HttpContext context) {
		if (_authentication.TransportTokenMatches(context)) {
			context.Response.Headers.AccessControlAllowOrigin = "*";
		} else if (context.Request.Headers.Origin.Count > 0 && !_authentication.CookieWebSocketOriginMatches(context)) {
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return false;
		}
		return true;
	}
}
