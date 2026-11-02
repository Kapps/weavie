using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Weavie.Hosting.Web;

/// <summary>Serves the built web app: Brotli siblings to clients that accept them, and hashed assets as immutable.</summary>
internal static class WorkspaceStaticAssets {
	private const string Brotli = ".br";
	private const string HashedAssets = "/assets";

	/// <summary>Adds static serving for <paramref name="assets"/> to the end of <paramref name="app"/>'s pipeline.</summary>
	public static void Use(WebApplication app, IFileProvider assets) {
		app.Use(async (context, next) => {
			string path = context.Request.Path.Value ?? "/";
			if (assets.GetFileInfo(path + Brotli).Exists) {
				context.Response.Headers.Vary = "Accept-Encoding";
				if (AcceptsBrotli(context.Request)) {
					context.Request.Path = path + Brotli;
					context.Response.Headers.ContentEncoding = "br";
				}
			}

			await next().ConfigureAwait(false);
		});
		app.UseStaticFiles(new StaticFileOptions {
			FileProvider = assets,
			ContentTypeProvider = new UncompressedContentTypes(),
			// Vite names everything under /assets/ by content hash, so a URL there never changes meaning.
			OnPrepareResponse = file => file.Context.Response.Headers.CacheControl =
				file.Context.Request.Path.StartsWithSegments(HashedAssets, StringComparison.Ordinal)
					? "public, max-age=31536000, immutable"
					: "no-cache",
		});
	}

	private static bool AcceptsBrotli(HttpRequest request) =>
		request.GetTypedHeaders().AcceptEncoding.Any(encoding =>
			string.Equals(encoding.Value.Value, "br", StringComparison.OrdinalIgnoreCase) && (encoding.Quality ?? 1) > 0);

	private sealed class UncompressedContentTypes : IContentTypeProvider {
		private readonly FileExtensionContentTypeProvider _types = new();

		public bool TryGetContentType(string subpath, out string contentType) =>
			_types.TryGetContentType(subpath.EndsWith(Brotli, StringComparison.Ordinal) ? subpath[..^Brotli.Length] : subpath, out contentType!);
	}
}
