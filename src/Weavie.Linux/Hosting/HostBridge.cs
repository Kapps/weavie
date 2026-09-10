using System.Runtime.InteropServices;
using Weavie.Hosting;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge. Inbound via the <c>weavie</c> script-message handler to
/// <see cref="MessageReceived"/>; outbound via <see cref="Broadcast"/>. Raw-JSON bodies, matching the macOS host.
/// </summary>
internal sealed class HostBridge : IWebTransportHub {
	// Kept alive: native holds a bare function pointer to this.
	private readonly ScriptMessageCallback _onScriptMessage;
	private readonly PolicyDecisionCallback _onPolicy;
	private IntPtr _webView;

	/// <summary>The shared document and message authentication boundary.</summary>
	public NativeBridgeSecurity Security { get; } = new();

	/// <summary>Call <see cref="RegisterOn"/> with the view's user-content manager to wire inbound messages.</summary>
	internal HostBridge() {
		_onScriptMessage = OnScriptMessage;
		_onPolicy = OnPolicy;
	}

	/// <summary>Raised with the raw JSON body of each inbound message (on the GTK main thread).</summary>
	public event Action<WebPeer, string>? MessageReceived;

	/// <inheritdoc/>
	public event Action<WebPeer>? PeerDisconnected {
		add { }
		remove { }
	}

	/// <summary>
	/// Registers the <c>weavie</c> script-message handler on <paramref name="userContentManager"/> and connects
	/// the delivery signal. Must be called before the page loads.
	/// </summary>
	internal void RegisterOn(IntPtr userContentManager) {
		WebKit.webkit_user_content_manager_register_script_message_handler(userContentManager, "weavie", IntPtr.Zero);
		_ = GLib.g_signal_connect_data(
			userContentManager,
			"script-message-received::weavie",
			Marshal.GetFunctionPointerForDelegate(_onScriptMessage),
			IntPtr.Zero,
			IntPtr.Zero,
			0);
	}

	/// <summary>Binds the bridge to the web view it pushes outbound messages into.</summary>
	internal void Attach(IntPtr webView) {
		_webView = webView;
		GLib.g_signal_connect_data(webView, "decide-policy", Marshal.GetFunctionPointerForDelegate(_onPolicy),
			IntPtr.Zero, IntPtr.Zero, 0);
	}

	private int OnPolicy(IntPtr view, IntPtr decision, int type, IntPtr data) {
		bool deny = type == 1;
		if (type == 2) {
			string? url = Marshal.PtrToStringUTF8(WebKit.webkit_uri_response_get_uri(
				WebKit.webkit_response_policy_decision_get_response(decision)));
			deny = !Security.Allows(url, WebKit.webkit_response_policy_decision_is_main_frame_main_resource(decision));
		}
		if (!deny) return 0;
		WebKit.webkit_policy_decision_ignore(decision);
		return 1;
	}

	/// <summary>Pushes a raw JSON message string into the page via <c>window.__weavieReceive</c> (on the main thread).</summary>
	public void Broadcast(WebTransportMessage message) {
		IntPtr webView = _webView;
		if (webView == IntPtr.Zero) {
			return;
		}

		string script = WebBridgeScript.Receive(message.Json);
		GtkMain.Invoke(() => WebKit.webkit_web_view_evaluate_javascript(
			webView, script, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
	}

	/// <inheritdoc/>
	public void Send(WebPeer peer, WebTransportMessage message) {
		if (peer == WebPeer.Native) {
			Broadcast(message);
		}
	}

	// Main thread: extract the JS value as a string, free WebKit's copy, and forward the raw JSON body.
	private void OnScriptMessage(IntPtr manager, IntPtr jsValue, IntPtr userData) {
		if (!WebKit.jsc_value_is_string(jsValue)) return;
		IntPtr stringPtr = WebKit.jsc_value_to_string(jsValue);
		string body = Marshal.PtrToStringUTF8(stringPtr) ?? string.Empty;
		GLib.g_free(stringPtr);
		if (Security.Authenticate(body) is { } authenticated) MessageReceived?.Invoke(WebPeer.Native, authenticated);
	}
}
