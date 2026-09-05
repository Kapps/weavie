using Foundation;
using Weavie.Hosting;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge: inbound <c>messageHandlers.weavie.postMessage</c> raises
/// <see cref="MessageReceived"/>; outbound <see cref="Broadcast"/> evaluates <c>window.__weavieReceive</c> on the
/// main thread. Bodies are raw JSON; typed dispatch lives on each side.
/// </summary>
public sealed class HostBridge : NSObject, IWKScriptMessageHandler, IWebTransportHub {
	private WKWebView? _webView;

	/// <summary>Raised with the raw JSON body of each inbound message (on the main thread).</summary>
	public event Action<WebPeer, string>? MessageReceived;

	/// <inheritdoc/>
	public event Action<WebPeer>? PeerDisconnected;

	/// <summary>Binds the bridge to the web view it pushes outbound messages into.</summary>
	public void Attach(WKWebView webView) => _webView = webView;

	/// <summary>WKWebView script-message callback: forwards the inbound body to <see cref="MessageReceived"/>.</summary>
	[Export("userContentController:didReceiveScriptMessage:")]
	public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message) =>
		GuardedUiDispatcher.Run(() => {
			if (_webView is not null) {
				MessageReceived?.Invoke(WebPeer.Native, message.Body?.ToString() ?? string.Empty);
			}
		}, MacUiFailure.Report);

	/// <summary>Pushes a raw JSON message string into the page via <c>window.__weavieReceive</c>.</summary>
	public void Broadcast(WebTransportMessage message) {
		var webView = _webView;
		if (webView is null) {
			return;
		}

		string script = WebBridgeScript.Receive(message.Json);

		// Always defer, never evaluate inline: a push made while handling an inbound web message (a palette/shortcut
		// command whose handler re-pushes a setting synchronously) would else re-enter EvaluateJavaScript from inside
		// the WKScriptMessage handler, where WebKit never runs it. Non-blocking, so a non-main caller (the PTY read
		// thread) never parks on a main-thread hop. Matches the Windows/Linux hosts, which likewise always defer.
		NSApplication.SharedApplication.BeginInvokeOnMainThread(() => GuardedUiDispatcher.Run(() => {
			if (!ReferenceEquals(_webView, webView)) {
				return;
			}
			webView.EvaluateJavaScript(script, (_, error) => GuardedUiDispatcher.Run(() => {
				if (error is not null) {
					Fail(new InvalidOperationException(error.LocalizedDescription));
				}
			}, MacUiFailure.Report));
		}, Fail));
	}

	/// <inheritdoc/>
	public void Send(WebPeer peer, WebTransportMessage message) {
		if (peer == WebPeer.Native) {
			Broadcast(message);
		}
	}

	private void Fail(Exception failure) {
		if (_webView is null) {
			return;
		}
		_webView = null;
		try {
			PeerDisconnected?.Invoke(WebPeer.Native);
		} catch (Exception disconnectFailure) {
			failure = new AggregateException(failure, disconnectFailure);
		}
		MacUiFailure.Report(new InvalidOperationException(
			"The editor connection failed. Close and reopen this window to reconnect.", failure));
	}
}
