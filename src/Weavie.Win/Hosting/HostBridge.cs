using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Hosting;

namespace Weavie.Win.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge over WebView2 (shared <see cref="IHostBridge"/>). Inbound: JS
/// <c>postMessage(json)</c> -&gt; <see cref="MessageReceived"/>. Outbound: <see cref="PostToWeb"/> posts
/// through WebView2's native message queue on the UI thread. Bodies are raw JSON strings.
/// </summary>
public sealed class HostBridge : IHostBridge, IDisposable {
	private CoreWebView2? _core;
	private OrderedMessageQueue? _outbound;

	/// <summary>Raised with the raw JSON body of each inbound message (on the UI thread).</summary>
	public event Action<string>? MessageReceived;

	/// <summary>Binds to the (already-initialized) WebView2 and starts listening for inbound web messages.</summary>
	public void Attach(WebView2 webView) {
		ArgumentNullException.ThrowIfNull(webView);
		var core = webView.CoreWebView2
			?? throw new InvalidOperationException("CoreWebView2 not initialized; call EnsureCoreWebView2Async first.");
		core.WebMessageReceived += OnWebMessageReceived;
		_core = core;
		_outbound = new OrderedMessageQueue(
			action => webView.BeginInvoke(action),
			core.PostWebMessageAsString);
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

	/// <summary>Pushes a raw JSON message string through WebView2's ordered host-to-page channel.</summary>
	public void PostToWeb(string json) => _outbound?.Enqueue(json);

	/// <summary>Stops outbound scheduling and detaches the inbound WebView2 handler.</summary>
	public void Dispose() {
		_outbound?.Dispose();
		_outbound = null;
		if (_core is not null) {
			_core.WebMessageReceived -= OnWebMessageReceived;
			_core = null;
		}
	}
}
