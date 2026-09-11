using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Hosting;

namespace Weavie.Win.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge over WebView2 (shared <see cref="IWebTransportHub"/>). Inbound: JS
/// <c>postMessage(json)</c> -&gt; <see cref="MessageReceived"/>. Outbound: <see cref="Broadcast"/> posts
/// through WebView2's native message queue on the UI thread. Bodies are raw JSON strings.
/// </summary>
public sealed class HostBridge : IWebTransportHub, IDisposable {
	private CoreWebView2? _core;

	/// <summary>The shared document and message authentication boundary.</summary>
	public NativeBridgeSecurity Security { get; } = new();
	private volatile OrderedMessageQueue? _outbound;

	/// <summary>Raised with the raw JSON body of each inbound message (on the UI thread).</summary>
	public event Action<WebPeer, string>? MessageReceived;

	/// <inheritdoc/>
	public event Action<WebPeer>? PeerDisconnected;

	/// <summary>Binds to the (already-initialized) WebView2 and starts listening for inbound web messages.</summary>
	public void Attach(WebView2 webView) {
		ArgumentNullException.ThrowIfNull(webView);
		var core = webView.CoreWebView2
			?? throw new InvalidOperationException("CoreWebView2 not initialized; call EnsureCoreWebView2Async first.");
		core.WebMessageReceived += OnWebMessageReceived;
		core.NavigationStarting += (_, e) => e.Cancel |= !Security.Allows(e.Uri, true);
		core.FrameNavigationStarting += (_, e) => e.Cancel |= !Security.Allows(e.Uri, false);
		core.NewWindowRequested += (_, e) => e.Handled = true;
		_core = core;
		_outbound = new OrderedMessageQueue(
			action => webView.BeginInvoke(action),
			script => _ = SendScriptAsync(core, script),
			failure => {
				// Scheduling can fail on a producer thread; native unsubscription stays in UI-owned Dispose.
				_outbound = null;
				try {
					PeerDisconnected?.Invoke(WebPeer.Native);
				} catch (Exception disconnectFailure) {
					failure = new AggregateException(failure, disconnectFailure);
				}
				WinUiFailure.Report(new InvalidOperationException(
					"The editor connection failed. Close and reopen this window to reconnect.", failure));
			});
	}

	private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
		if (_outbound is null || !ReferenceEquals(sender, _core) || !Security.Allows(e.Source, true)) {
			return;
		}
		string body;
		try {
			body = e.TryGetWebMessageAsString();
		} catch (ArgumentException) {
			return;
		}

		if (Security.Authenticate(body) is { } authenticated) MessageReceived?.Invoke(WebPeer.Native, authenticated);
	}

	/// <summary>Pushes a raw JSON message string through WebView2's ordered host-to-page channel.</summary>
	public void Broadcast(WebTransportMessage message) => _outbound?.Enqueue(WebBridgeScript.Receive(message.Json));

	/// <inheritdoc/>
	public void Send(WebPeer peer, WebTransportMessage message) {
		if (peer == WebPeer.Native) {
			Broadcast(message);
		}
	}

	private async Task SendScriptAsync(CoreWebView2 core, string script) {
		try {
			await core.ExecuteScriptAsync(script);
		} catch (Exception failure) {
			Dispose();
			PeerDisconnected?.Invoke(WebPeer.Native);
			WinUiFailure.Report(failure);
		}
	}

	/// <summary>Stops outbound scheduling and detaches the inbound WebView2 handler.</summary>
	public void Dispose() {
		Security.Revoke();
		_outbound?.Dispose();
		_outbound = null;
		_core?.WebMessageReceived -= OnWebMessageReceived;
		_core = null;
	}
}
