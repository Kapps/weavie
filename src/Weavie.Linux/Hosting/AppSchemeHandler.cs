using System.Runtime.InteropServices;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// Serves the built Solid/Vite web app to the WebKit view over a custom <c>app://</c> scheme,
/// straight from the on-disk <c>wwwroot</c> next to the host exe — no network, no localhost port,
/// secure same-origin context (so workers and the Event Timing API behave). Files are read from disk
/// and returned with a correct MIME type (WebKit refuses ES modules served with the wrong type).
/// </summary>
internal sealed class AppSchemeHandler {
	private readonly string _root;

	// Kept alive for the lifetime of the handler: native holds bare function pointers to these.
	private readonly UriSchemeRequestCallback _onRequest;
	private readonly GDestroyNotify _freeBuffer;

	/// <summary>Creates a handler that serves files from <paramref name="wwwroot"/> (resolved to a full path).</summary>
	internal AppSchemeHandler(string wwwroot) {
		ArgumentException.ThrowIfNullOrEmpty(wwwroot);
		_root = Path.GetFullPath(wwwroot);
		_onRequest = OnRequest;
		_freeBuffer = Marshal.FreeHGlobal;
	}

	/// <summary>Registers the <c>app://</c> scheme on <paramref name="context"/> (the view's web context). Call before loading.</summary>
	internal void Register(IntPtr context) =>
		WebKit.webkit_web_context_register_uri_scheme(
			context, "app", Marshal.GetFunctionPointerForDelegate(_onRequest), IntPtr.Zero, IntPtr.Zero);

	// Serves the requested app:// URL from the web root: maps "/" to index.html, rejects path-traversal
	// escapes, and returns the file bytes with a correct MIME type (a small text/plain 404 otherwise).
	private void OnRequest(IntPtr request, IntPtr userData) {
		IntPtr pathPtr = WebKit.webkit_uri_scheme_request_get_path(request);
		string requestedPath = Marshal.PtrToStringUTF8(pathPtr) ?? "/"; // owned by the request; do not free.
		if (string.IsNullOrEmpty(requestedPath) || requestedPath == "/") {
			requestedPath = "/index.html";
		}

		string resolved = Path.GetFullPath(Path.Combine(_root, requestedPath.TrimStart('/')));

		// Defend against path traversal escaping the web root.
		bool insideRoot = resolved == _root
			|| resolved.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
		if (!insideRoot || !File.Exists(resolved)) {
			Serve(request, "Not Found"u8.ToArray(), "text/plain");
			return;
		}

		Serve(request, File.ReadAllBytes(resolved), MimeFor(Path.GetExtension(resolved)));
	}

	// Hands WebKit a memory input stream over a native copy of the bytes (freed by the GDestroyNotify
	// once WebKit is done reading), then unrefs the stream — WebKit holds its own reference.
	private void Serve(IntPtr request, byte[] bytes, string mime) {
		IntPtr buffer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
		Marshal.Copy(bytes, 0, buffer, bytes.Length);
		IntPtr stream = GLib.g_memory_input_stream_new_from_data(
			buffer, bytes.Length, Marshal.GetFunctionPointerForDelegate(_freeBuffer));
		WebKit.webkit_uri_scheme_request_finish(request, stream, bytes.Length, mime);
		GLib.g_object_unref(stream);
	}

	private static string MimeFor(string extension) => extension.ToLowerInvariant() switch {
		".html" => "text/html",
		".js" or ".mjs" => "text/javascript",
		".css" => "text/css",
		".json" or ".map" => "application/json",
		".svg" => "image/svg+xml",
		".png" => "image/png",
		".woff" => "font/woff",
		".woff2" => "font/woff2",
		".ttf" => "font/ttf",
		".wasm" => "application/wasm",
		_ => "application/octet-stream",
	};
}
