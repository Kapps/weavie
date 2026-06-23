using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Hosting;

namespace Weavie.Win.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge over WebView2 (shared <see cref="IHostBridge"/>). Inbound: JS
/// <c>postMessage(json)</c> -&gt; <see cref="MessageReceived"/>. Outbound: <see cref="PostToWeb"/> evaluates
/// <c>window.__weavieReceive(json)</c> on the UI thread. Bodies are raw JSON strings.
/// </summary>
public sealed class HostBridge : IHostBridge {
	private WebView2? _webView;
	private CoreWebView2? _core;

	/// <summary>Raised with the raw JSON body of each inbound message (on the UI thread).</summary>
	public event Action<string>? MessageReceived;

	/// <summary>Binds to the (already-initialized) WebView2 and starts listening for inbound web messages.</summary>
	public void Attach(WebView2 webView) {
		ArgumentNullException.ThrowIfNull(webView);
		_webView = webView;
		_core = webView.CoreWebView2
			?? throw new InvalidOperationException("CoreWebView2 not initialized; call EnsureCoreWebView2Async first.");
		_core.WebMessageReceived += OnWebMessageReceived;
	}

	private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
		string body;
		try {
			body = e.TryGetWebMessageAsString();
		} catch (ArgumentException) {
			// Non-string payload — defensive; the frontend only ever posts JSON strings.
			body = e.WebMessageAsJson;
		}

		MessageReceived?.Invoke(body ?? string.Empty);
	}

	/// <summary>Pushes a raw JSON message string into the page via <c>window.__weavieReceive</c>.</summary>
	public void PostToWeb(string json) {
		var webView = _webView;
		var core = _core;
		if (webView is null || core is null) {
			return;
		}

		string script = WebBridgeScript.Receive(json);

		// ExecuteScriptAsync must run on the UI thread; PTY output arrives off-thread.
		if (webView.InvokeRequired) {
			webView.BeginInvoke(() => _ = core.ExecuteScriptAsync(script));
		} else {
			_ = core.ExecuteScriptAsync(script);
		}
	}
}
