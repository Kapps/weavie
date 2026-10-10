using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Weavie.Hosting.Web;

public sealed partial class WorkspaceHttpServer {
	private Task ServeMediaAsync(HttpContext context) {
		string fileName = context.Request.RouteValues["fileName"]?.ToString() ?? string.Empty;
		string path = context.Request.Query["path"].ToString();
		var resource = string.Equals(fileName, Path.GetFileName(path), StringComparison.Ordinal)
			? _files.Open(context.Request.Query["session"].ToString(), path)
			: null;
		return ServeFileAsync(context, resource);
	}

	private Task ServePreviewAssetAsync(HttpContext context) {
		var resource = _files.OpenPreviewAsset(
			context.Request.RouteValues["grant"]?.ToString() ?? string.Empty,
			context.Request.RouteValues["path"]?.ToString() ?? string.Empty);
		// Opened directly, a page still gets no origin; the opaque-origin frame fetches modules and fonts with CORS.
		context.Response.Headers.ContentSecurityPolicy = "sandbox allow-scripts";
		context.Response.Headers.AccessControlAllowOrigin = "*";
		return ServeFileAsync(context, resource);
	}

	private static async Task ServeFileAsync(HttpContext context, MediaResource? resource) {
		if (resource is null) {
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		context.Response.Headers.CacheControl = "private, no-cache";
		context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
		context.Response.Headers["Referrer-Policy"] = "no-referrer";
		string tag = $"\"{resource.LastModified.ToUnixTimeMilliseconds():x}-{resource.Length:x}\"";
		var result = Results.File(
			resource.Stream,
			resource.ContentType,
			fileDownloadName: null,
			resource.LastModified,
			new EntityTagHeaderValue(tag, isWeak: true),
			enableRangeProcessing: true);
		await result.ExecuteAsync(context).ConfigureAwait(false);
	}
}
