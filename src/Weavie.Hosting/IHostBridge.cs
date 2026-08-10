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
/// The message-bus lane whose publication order an outbound transport must preserve. Host routes leave
/// <see cref="Slot"/> and <see cref="Incarnation"/> empty; session routes carry their exact address.
/// </summary>
public readonly record struct WebMessageRoute {
	/// <summary>Creates one host or exact-session feature route.</summary>
	public WebMessageRoute(string slot, string incarnation, string feature) {
		ArgumentNullException.ThrowIfNull(slot);
		ArgumentNullException.ThrowIfNull(incarnation);
		ArgumentException.ThrowIfNullOrEmpty(feature);
		if (string.IsNullOrEmpty(slot) != string.IsNullOrEmpty(incarnation)) {
			throw new ArgumentException("A transport route requires both session address parts or neither.");
		}

		Slot = slot;
		Incarnation = incarnation;
		Feature = feature;
	}

	/// <summary>The owning session slot, or empty for a host route.</summary>
	public string Slot { get; }

	/// <summary>The owning session incarnation, or empty for a host route.</summary>
	public string Incarnation { get; }

	/// <summary>The message-bus feature lane.</summary>
	public string Feature { get; }
}

/// <summary>A serialized message plus the bus route whose outbound order it belongs to.</summary>
public sealed record WebTransportMessage {
	/// <summary>Creates an outbound transport message.</summary>
	public WebTransportMessage(WebMessageRoute route, string json) {
		ArgumentNullException.ThrowIfNull(json);
		Route = route;
		Json = json;
	}

	/// <summary>The lane whose messages must arrive in publication order.</summary>
	public WebMessageRoute Route { get; }

	/// <summary>The serialized message envelope.</summary>
	public string Json { get; }
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
	void Broadcast(WebTransportMessage message);

	/// <summary>Pushes a response to one exact attached page.</summary>
	void Send(WebPeer peer, WebTransportMessage message);
}
