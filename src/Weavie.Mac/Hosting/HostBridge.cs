using Foundation;
using Weavie.Hosting;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge: inbound <c>messageHandlers.weavie.postMessage</c> raises
/// <see cref="MessageReceived"/>; outbound <see cref="PostToWeb"/> evaluates <c>window.__weavieReceive</c> on the
/// main thread. Bodies are raw JSON; typed dispatch lives on each side.
/// </summary>
public sealed class HostBridge : NSObject, IWKScriptMessageHandler, IHostBridge {
	private WKWebView? _webView;

	/// <summary>Raised with the raw JSON body of each inbound message (on the main thread).</summary>
	public event Action<string>? MessageReceived;

	/// <summary>Binds the bridge to the web view it pushes outbound messages into.</summary>
	public void Attach(WKWebView webView) => _webView = webView;

	/// <summary>WKWebView script-message callback: forwards the inbound body to <see cref="MessageReceived"/>.</summary>
	[Export("userContentController:didReceiveScriptMessage:")]
	public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message) => MessageReceived?.Invoke(message.Body?.ToString() ?? string.Empty);

	/// <summary>Pushes a raw JSON message string into the page via <c>window.__weavieReceive</c>.</summary>
	public void PostToWeb(string json) {
		var webView = _webView;
		if (webView is null) {
			return;
		}

		string script = WebBridgeScript.Receive(json);

		// Always defer, never evaluate inline: a push made while handling an inbound web message (a palette/shortcut
		// command whose handler re-pushes a setting synchronously) would else re-enter EvaluateJavaScript from inside
		// the WKScriptMessage handler, where WebKit never runs it. Non-blocking, so a non-main caller (the PTY read
		// thread) never parks on a main-thread hop. Matches the Windows/Linux hosts, which likewise always defer.
		NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
			webView.EvaluateJavaScript(script, (_, error) => LogIfError(error)));
	}

	private static void LogIfError(NSError? error) {
		if (error is not null) {
			Console.Error.WriteLine($"[weavie] EvaluateJavaScript error: {error.LocalizedDescription}");
		}
	}
}
