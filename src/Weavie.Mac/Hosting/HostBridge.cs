using System.Text.Json;
using Foundation;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge.
///   inbound:  JS calls <c>window.webkit.messageHandlers.weavie.postMessage(json)</c> -&gt; <see cref="MessageReceived"/>.
///   outbound: <see cref="PostToWeb"/> evaluates <c>window.__weavieReceive(json)</c> on the main thread.
/// Bodies are raw JSON strings; typed dispatch lives on each side.
/// </summary>
public sealed class HostBridge : NSObject, IWKScriptMessageHandler {
	private WKWebView? _webView;

	/// <summary>Raised with the raw JSON body of each inbound message (on the main thread).</summary>
	public event Action<string>? MessageReceived;

	/// <summary>Binds the bridge to the web view it pushes outbound messages into.</summary>
	public void Attach(WKWebView webView) => _webView = webView;

	/// <summary>
	/// WKWebView script-message callback: forwards the inbound message body to
	/// <see cref="MessageReceived"/> as a raw JSON string.
	/// </summary>
	[Export("userContentController:didReceiveScriptMessage:")]
	public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message) => MessageReceived?.Invoke(message.Body?.ToString() ?? string.Empty);

	/// <summary>Pushes a raw JSON message string into the page via <c>window.__weavieReceive</c>.</summary>
	public void PostToWeb(string json) {
		var webView = _webView;
		if (webView is null) {
			return;
		}

		// Encode the JSON payload as a JS string literal argument (trim-safe; no reflection).
		string literal = $"\"{JsonEncodedText.Encode(json)}\"";
		string script = $"window.__weavieReceive && window.__weavieReceive({literal});";

		if (NSThread.IsMain) {
			webView.EvaluateJavaScript(script, (_, error) => LogIfError(error));
		} else {
			NSApplication.SharedApplication.InvokeOnMainThread(() =>
				webView.EvaluateJavaScript(script, (_, error) => LogIfError(error)));
		}
	}

	private static void LogIfError(NSError? error) {
		if (error is not null) {
			Console.Error.WriteLine($"[weavie] EvaluateJavaScript error: {error.LocalizedDescription}");
		}
	}
}
