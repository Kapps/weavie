namespace Weavie.Hosting;

/// <summary>
/// Opaque identity for one page attached to a host transport. Application features never receive this value;
/// the message router uses it to return responses to the physical peer that issued a request.
/// </summary>
public readonly record struct WebPeer {
	/// <summary>Creates an opaque transport peer.</summary>
	public WebPeer(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		Id = id;
	}

	/// <summary>The transport-owned identity.</summary>
	public string Id { get; }

	/// <summary>The sole page in an in-process native window.</summary>
	public static WebPeer Native { get; } = new("native");
}

/// <summary>
/// The raw page transport shared by every host shell. It preserves physical peer identity; only the message
/// router should expose it to application code.
/// </summary>
public interface IWebTransportHub {
	/// <summary>
	/// Raised with an inbound peer and raw JSON body on the transport's callback thread. A subscriber must only
	/// enqueue or dispatch the body; callback affinity is not an application-code execution context.
	/// </summary>
	event Action<WebPeer, string>? MessageReceived;

	/// <summary>Raised after a physical peer disconnects; subscribers must enqueue lifecycle handling and return.</summary>
	event Action<WebPeer>? PeerDisconnected;

	/// <summary>Pushes an event to every attached page.</summary>
	void Broadcast(string json);

	/// <summary>Pushes a response to one exact attached page.</summary>
	void Send(WebPeer peer, string json);
}
