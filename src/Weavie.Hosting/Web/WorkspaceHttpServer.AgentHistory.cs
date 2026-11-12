using Microsoft.AspNetCore.Http;
using Weavie.Hosting.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Web;

public sealed partial class WorkspaceHttpServer {
	private async Task ServeAgentHistoryAsync(HttpContext context) {
		if (SessionResource(context, "no-store") is not { } address) return;

		var request = new AgentPaneHistoryRequest(
			ReadRevision(context, "knownGeneration"),
			ReadRevision(context, "knownRevision"));
		context.Response.ContentType = "application/x-ndjson";
		try {
			if (!await _core.WriteAgentHistoryAsync(
				address,
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
