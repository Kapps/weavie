namespace Weavie.Hosting;

/// <summary>
/// The JS &lt;-&gt; C# message bridge contract shared by the native shells, each over its web view: inbound the
/// page posts JSON → <see cref="MessageReceived"/>, outbound <see cref="PostToWeb"/> evaluates
/// <c>window.__weavieReceive(json)</c> on the UI thread. Bodies are raw JSON; the host depends only on this.
/// </summary>
public interface IHostBridge {
	/// <summary>Raised with the raw JSON body of each inbound message (on the UI thread).</summary>
	event Action<string>? MessageReceived;

	/// <summary>Pushes a raw JSON message string into the page via <c>window.__weavieReceive</c>.</summary>
	void PostToWeb(string json);
}

/// <summary>A bridge that can identify when a browser page disconnects from its host.</summary>
public interface IPageLifecycleHostBridge {
	/// <summary>Raised after the last connection for a page id has closed.</summary>
	event Action<string>? PageDisconnected;
}
