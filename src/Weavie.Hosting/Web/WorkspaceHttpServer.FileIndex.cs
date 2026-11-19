using Microsoft.AspNetCore.Http;

namespace Weavie.Hosting.Web;

public sealed partial class WorkspaceHttpServer {
	private async Task ServeFileIndexAsync(HttpContext context) {
		if (SessionResource(context, "no-store") is not { } address) return;

		long? knownRevision = ReadRevision(context, "knownRevision");
		context.Response.ContentType = "application/x-ndjson";
		if (!await _core.WriteFileIndexAsync(address, knownRevision, context.Response.Body, context.RequestAborted).ConfigureAwait(false)) {
			context.Response.StatusCode = StatusCodes.Status404NotFound;
		}
	}
}
