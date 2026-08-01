using System.Text.Json;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Tests;

/// <summary>
/// An in-memory <see cref="IWebTransportHub"/> for tests: captures every <see cref="Broadcast"/> message in order
/// and lets a test raise an inbound page message via <see cref="Receive(string)"/> (driving <c>HostCore.OnWebMessage</c>
/// exactly as the real web view would). The shared host components depend only on the bridge contract, so this
/// is all they need to exercise routing end-to-end without a web view.
/// </summary>
internal sealed class FakeHostBridge : IWebTransportHub {
	private readonly List<string> _posted = [];
	private readonly List<string> _broadcasts = [];
	private readonly List<(WebPeer Peer, string Json)> _sent = [];
	private readonly Lock _gate = new();

	public event Action<WebPeer, string>? MessageReceived;
	public event Action<WebPeer>? PeerDisconnected;

	public Func<MessageEnvelope, FakeWebResponse?>? RequestResponder { get; set; }

	/// <summary>Whether a live host is subscribed to inbound page messages.</summary>
	public bool HasMessageReceiver => MessageReceived is not null;

	public void Broadcast(string json) {
		lock (_gate) {
			_broadcasts.Add(json);
			_posted.Add(json);
		}
	}

	public void Send(WebPeer peer, string json) {
		lock (_gate) {
			_sent.Add((peer, json));
			_posted.Add(json);
		}
		if (RequestResponder is not { } responder
			|| !MessageEnvelope.TryParse(json, out var envelope)
			|| envelope is not { Kind: MessageKind.Request }
			|| responder(envelope) is not { } response) {
			return;
		}

		MessageReceived?.Invoke(
			peer,
			MessageEnvelope.Response(
				envelope.Scope,
				envelope.Session,
				envelope.RequestId!,
				envelope.Feature,
				envelope.Name,
				response.Payload,
				response.Error).ToJson());
	}

	/// <summary>Every unicast message and its exact destination.</summary>
	public IReadOnlyList<(WebPeer Peer, string Json)> Sent {
		get {
			lock (_gate) {
				return [.. _sent];
			}
		}
	}

	/// <summary>Every frame sent through the transport's fan-out path.</summary>
	public IReadOnlyList<string> Broadcasts {
		get {
			lock (_gate) {
				return [.. _broadcasts];
			}
		}
	}

	/// <summary>Every message posted to the page, in order.</summary>
	public IReadOnlyList<string> Posted {
		get {
			lock (_gate) {
				return [.. _posted];
			}
		}
	}

	/// <summary>Every event published by one message-bus feature and name, returned as payloads.</summary>
	public IReadOnlyList<JsonElement> PostedEvents(string feature, string name) {
		var result = new List<JsonElement>();
		foreach (string json in Posted) {
			if (MessageEnvelope.TryParse(json, out var envelope)
				&& envelope is { Kind: MessageKind.Event }
				&& envelope.Feature == feature
				&& envelope.Name == name) {
				result.Add(envelope.Payload);
			}
		}

		return result;
	}

	/// <summary>Every event published by one exact session feature and name, returned as payloads.</summary>
	public IReadOnlyList<JsonElement> PostedEvents(
		SessionAddress session,
		string feature,
		string name) {
		var result = new List<JsonElement>();
		foreach (string json in Posted) {
			if (MessageEnvelope.TryParse(json, out var envelope)
				&& envelope is { Kind: MessageKind.Event }
				&& envelope.Session == session
				&& envelope.Feature == feature
				&& envelope.Name == name) {
				result.Add(envelope.Payload);
			}
		}

		return result;
	}

	/// <summary>The last event payload for one message-bus feature and name.</summary>
	public JsonElement? LastEvent(string feature, string name) {
		var all = PostedEvents(feature, name);
		return all.Count == 0 ? null : all[^1];
	}

	/// <summary>The last event payload for one exact session feature and name.</summary>
	public JsonElement? LastEvent(SessionAddress session, string feature, string name) {
		var all = PostedEvents(session, feature, name);
		return all.Count == 0 ? null : all[^1];
	}

	/// <summary>Every event with one name, returned as payloads regardless of its owning feature.</summary>
	public IReadOnlyList<JsonElement> PostedEventsNamed(string name) {
		var result = new List<JsonElement>();
		foreach (string json in Posted) {
			if (MessageEnvelope.TryParse(json, out var envelope)
				&& envelope is { Kind: MessageKind.Event }
				&& envelope.Name == name) {
				result.Add(envelope.Payload);
			}
		}

		return result;
	}

	/// <summary>The last event payload with one name.</summary>
	public JsonElement? LastEventNamed(string name) {
		var all = PostedEventsNamed(name);
		return all.Count == 0 ? null : all[^1];
	}

	/// <summary>Creates one session-owned feature channel that publishes into this transport.</summary>
	public MessageFeatureChannel SessionFeature(string feature) {
		var bus = new SessionMessageBus(
			new SessionAddress("test", Guid.NewGuid().ToString("n")),
			Broadcast,
			Send,
			_ => { });
		return bus.Feature(feature);
	}

	/// <summary>Creates one feature on an attached session view and targets its events to this transport.</summary>
	public ViewFeatureChannel SessionViewFeature(string feature) {
		var router = new HostMessageRouter(this, new InlineUiDispatcher(), _ => { });
		var endpoint = router.OpenSession(
			new SessionAddress("test", Guid.NewGuid().ToString("n")));
		endpoint.Activate();
		router.RouteAsync(
		WebPeer.Native,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"attach",
				JsonSerializer.SerializeToElement(new { pageEpoch = "fake-page" })).ToJson()).GetAwaiter().GetResult();
		return endpoint.View.Feature(feature);
	}

	/// <summary>Forgets every captured message (so a test can assert only on what happens next).</summary>
	public void Clear() {
		lock (_gate) {
			_posted.Clear();
			_broadcasts.Clear();
			_sent.Clear();
		}
	}

	/// <summary>Raises an inbound web message, as the page's bridge would.</summary>
	public void Receive(string json) => MessageReceived?.Invoke(WebPeer.Native, json);

	/// <summary>Raises an inbound message from one exact page.</summary>
	public void Receive(WebPeer peer, string json) => MessageReceived?.Invoke(peer, json);

	/// <summary>Raises the connection lifecycle signal for <paramref name="peer"/>.</summary>
	public void Disconnect(WebPeer peer) => PeerDisconnected?.Invoke(peer);
}

internal sealed record FakeWebResponse(JsonElement Payload, string? Error);
