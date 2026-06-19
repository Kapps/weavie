namespace Weavie.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge contract shared by the native shells. Each platform implements it
/// over its web view:
///   inbound:  the page calls <c>window.webkit.messageHandlers.weavie.postMessage(json)</c> -&gt; <see cref="MessageReceived"/>.
///   outbound: <see cref="PostToWeb"/> evaluates <c>window.__weavieReceive(json)</c> on the UI thread.
/// Bodies are raw JSON strings; typed dispatch lives on each side. The shared host components below
/// depend only on this contract, never on a concrete web view.
/// </summary>
public interface IHostBridge {
	/// <summary>Raised with the raw JSON body of each inbound message (on the UI thread).</summary>
	event Action<string>? MessageReceived;

	/// <summary>Pushes a raw JSON message string into the page via <c>window.__weavieReceive</c>.</summary>
	void PostToWeb(string json);
}
