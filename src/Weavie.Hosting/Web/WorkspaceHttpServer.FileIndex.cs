using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Weavie.Core.Git;

namespace Weavie.Hosting.Web;

public sealed partial class WorkspaceHttpServer {
	private async Task ServeFileIndexAsync(HttpContext context) {
		if (SessionResource(context, "no-store") is not { } address) return;

		long? knownRevision = ReadRevision(context, "knownRevision");
		GZipStream? compressed = null;
		try {
			if (!await _core.WriteFileIndexAsync(address, knownRevision, Open, context.RequestAborted).ConfigureAwait(false)) {
				context.Response.StatusCode = StatusCodes.Status404NotFound;
			}
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException && !context.Response.HasStarted) {
			// The page that asked reports the failure, in the listing's own words.
			context.Response.StatusCode = StatusCodes.Status500InternalServerError;
			context.Response.ContentType = "text/plain; charset=utf-8";
			await context.Response.WriteAsync(ex.Message, context.RequestAborted).ConfigureAwait(false);
		} finally {
			if (compressed is not null) {
				await compressed.DisposeAsync().ConfigureAwait(false);
			}
		}

		// A list is tens of megabytes of paths sharing their prefixes; a remote page downloads a tenth of it.
		Stream Open() {
			context.Response.ContentType = "application/x-ndjson";
			context.Response.Headers.Vary = "Accept-Encoding";
			if (!WorkspaceStaticAssets.Accepts(context.Request, "gzip")) {
				return context.Response.Body;
			}

			context.Response.Headers.ContentEncoding = "gzip";
			return compressed = new GZipStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
		}
	}
}
