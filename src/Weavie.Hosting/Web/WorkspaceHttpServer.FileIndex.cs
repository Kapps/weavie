using Microsoft.AspNetCore.Http;
using Weavie.Core.Git;

namespace Weavie.Hosting.Web;

public sealed partial class WorkspaceHttpServer {
	private async Task ServeFileIndexAsync(HttpContext context) {
		if (SessionResource(context, "no-store") is not { } address) return;

		long? knownRevision = ReadRevision(context, "knownRevision");
		context.Response.ContentType = "application/x-ndjson";
		try {
			if (!await _core.WriteFileIndexAsync(address, knownRevision, context.Response.Body, context.RequestAborted).ConfigureAwait(false)) {
				context.Response.StatusCode = StatusCodes.Status404NotFound;
			}
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException && !context.Response.HasStarted) {
			// The page that asked reports the failure, in the listing's own words.
			context.Response.StatusCode = StatusCodes.Status500InternalServerError;
			context.Response.ContentType = "text/plain; charset=utf-8";
			await context.Response.WriteAsync(ex.Message, context.RequestAborted).ConfigureAwait(false);
		}
	}
}
