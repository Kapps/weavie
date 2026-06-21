using System.Runtime.InteropServices;
using Weavie.Hosting;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge.
///   inbound:  JS calls <c>window.webkit.messageHandlers.weavie.postMessage(json)</c> -&gt; <see cref="MessageReceived"/>.
///   outbound: <see cref="PostToWeb"/> evaluates <c>window.__weavieReceive(json)</c> on the GTK main thread.
/// Bodies are raw JSON strings; typed dispatch lives on each side. The web app speaks the same
/// <c>window.webkit.messageHandlers</c> / <c>window.__weavieReceive</c> contract as the macOS WKWebView host,
/// so no web changes are needed.
/// </summary>
internal sealed class HostBridge : IHostBridge {
	// Kept alive for the lifetime of the bridge: native holds a bare function pointer to this.
	private readonly ScriptMessageCallback _onScriptMessage;
	private IntPtr _webView;

	/// <summary>Creates a bridge; call <see cref="RegisterOn"/> with the view's user-content manager to wire inbound messages.</summary>
	internal HostBridge() {
		_onScriptMessage = OnScriptMessage;
	}

	/// <summary>Raised with the raw JSON body of each inbound message (on the GTK main thread).</summary>
	public event Action<string>? MessageReceived;

	/// <summary>
	/// Registers the <c>weavie</c> script-message handler on <paramref name="userContentManager"/> and connects
	/// the signal that delivers inbound messages. Must be called before the page loads.
	/// </summary>
	internal void RegisterOn(IntPtr userContentManager) {
		WebKit.webkit_user_content_manager_register_script_message_handler(userContentManager, "weavie");
		_ = GLib.g_signal_connect_data(
			userContentManager,
			"script-message-received::weavie",
			Marshal.GetFunctionPointerForDelegate(_onScriptMessage),
			IntPtr.Zero,
			IntPtr.Zero,
			0);
	}

	/// <summary>Binds the bridge to the web view it pushes outbound messages into.</summary>
	internal void Attach(IntPtr webView) => _webView = webView;

	/// <summary>Pushes a raw JSON message string into the page via <c>window.__weavieReceive</c> (on the main thread).</summary>
	public void PostToWeb(string json) {
		IntPtr webView = _webView;
		if (webView == IntPtr.Zero) {
			return;
		}

		string script = WebBridgeScript.Receive(json);
		GtkMain.Invoke(() => WebKit.webkit_web_view_evaluate_javascript(
			webView, script, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
	}

	// WebKit script-message callback (on the main thread): pull the JS value out of the result, convert
	// it to a string, free WebKit's copy, and forward the raw JSON body.
	private void OnScriptMessage(IntPtr manager, IntPtr jsResult, IntPtr userData) {
		IntPtr value = WebKit.webkit_javascript_result_get_js_value(jsResult);
		IntPtr stringPtr = WebKit.jsc_value_to_string(value);
		string body = Marshal.PtrToStringUTF8(stringPtr) ?? string.Empty;
		GLib.g_free(stringPtr);
		MessageReceived?.Invoke(body);
	}
}
