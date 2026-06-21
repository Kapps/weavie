using System.Runtime.InteropServices;
using Weavie.Hosting.Web;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// Serves the built Solid/Vite web app to the WebKit view over a custom <c>app://</c> scheme, straight from the
/// on-disk <c>wwwroot</c> next to the host exe — no network, no localhost port, secure same-origin context (so
/// workers and the Event Timing API behave). Path resolution + MIME come from the shared
/// <see cref="WwwrootFileResolver"/>; this owns only the native WebKitGTK request/response binding.
/// </summary>
internal sealed class AppSchemeHandler {
	private readonly WwwrootFileResolver _resolver;

	// Kept alive for the lifetime of the handler: native holds bare function pointers to these.
	private readonly UriSchemeRequestCallback _onRequest;
	private readonly GDestroyNotify _freeBuffer;

	/// <summary>Creates a handler that serves files from <paramref name="wwwroot"/> (resolved to a full path).</summary>
	internal AppSchemeHandler(string wwwroot) {
		_resolver = new WwwrootFileResolver(wwwroot);
		_onRequest = OnRequest;
		_freeBuffer = Marshal.FreeHGlobal;
	}

	/// <summary>Registers the <c>app://</c> scheme on <paramref name="context"/> (the view's web context). Call before loading.</summary>
	internal void Register(IntPtr context) =>
		WebKit.webkit_web_context_register_uri_scheme(
			context, "app", Marshal.GetFunctionPointerForDelegate(_onRequest), IntPtr.Zero, IntPtr.Zero);

	// Serves the requested app:// URL via the shared resolver; on a 404 the resolver returns a small text/plain
	// "Not Found" body, which we serve as-is.
	private void OnRequest(IntPtr request, IntPtr userData) {
		IntPtr pathPtr = WebKit.webkit_uri_scheme_request_get_path(request);
		string requestedPath = Marshal.PtrToStringUTF8(pathPtr) ?? "/"; // owned by the request; do not free.
		var response = _resolver.Resolve(requestedPath);
		Serve(request, response.Bytes, response.Mime);
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
}
