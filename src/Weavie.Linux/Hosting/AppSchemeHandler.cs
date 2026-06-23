using System.Runtime.InteropServices;
using Weavie.Hosting.Web;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// Serves the web app over a custom <c>app://</c> scheme from on-disk <c>wwwroot</c> — a network-free secure
/// same-origin context. Path/MIME resolution lives in <see cref="WwwrootFileResolver"/>; this owns only the
/// native WebKitGTK request/response binding.
/// </summary>
internal sealed class AppSchemeHandler {
	private readonly WwwrootFileResolver _resolver;

	// Kept alive: native holds bare function pointers to these.
	private readonly UriSchemeRequestCallback _onRequest;
	private readonly GDestroyNotify _freeBuffer;

	/// <summary>Creates a handler that serves files from <paramref name="wwwroot"/>.</summary>
	internal AppSchemeHandler(string wwwroot) {
		_resolver = new WwwrootFileResolver(wwwroot);
		_onRequest = OnRequest;
		_freeBuffer = Marshal.FreeHGlobal;
	}

	/// <summary>Registers the <c>app://</c> scheme on the view's web <paramref name="context"/>. Call before loading.</summary>
	internal void Register(IntPtr context) =>
		WebKit.webkit_web_context_register_uri_scheme(
			context, "app", Marshal.GetFunctionPointerForDelegate(_onRequest), IntPtr.Zero, IntPtr.Zero);

	// WebKitGTK serves in-scheme fetches as same-origin and finish() answers 200, so the resolver's status + CORS
	// header (an Apple-WebKit workaround) are no-ops here; only the content type is marshaled.
	private void OnRequest(IntPtr request, IntPtr userData) {
		IntPtr pathPtr = WebKit.webkit_uri_scheme_request_get_path(request);
		string requestedPath = Marshal.PtrToStringUTF8(pathPtr) ?? "/"; // owned by the request; do not free.
		var response = _resolver.Resolve(requestedPath);
		Serve(request, response.Bytes, response.ContentType);
	}

	// Hands WebKit a memory input stream over a native copy of the bytes (freed by the GDestroyNotify
	// when WebKit is done), then unrefs the stream — WebKit holds its own reference.
	private void Serve(IntPtr request, byte[] bytes, string mime) {
		IntPtr buffer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
		Marshal.Copy(bytes, 0, buffer, bytes.Length);
		IntPtr stream = GLib.g_memory_input_stream_new_from_data(
			buffer, bytes.Length, Marshal.GetFunctionPointerForDelegate(_freeBuffer));
		WebKit.webkit_uri_scheme_request_finish(request, stream, bytes.Length, mime);
		GLib.g_object_unref(stream);
	}
}
