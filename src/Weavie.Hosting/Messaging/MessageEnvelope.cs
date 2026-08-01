using System.Text.Json;

namespace Weavie.Hosting.Messaging;

internal enum MessageScope {
	Host,
	Session,
}

internal enum MessageKind {
	Event,
	Request,
	Response,
	Cancel,
}

internal sealed record MessageEnvelope(
	MessageScope Scope,
	SessionAddress? Session,
	MessageKind Kind,
	string? RequestId,
	string Feature,
	string Name,
	JsonElement Payload,
	string? Error) {
	public static MessageEnvelope Event(
		MessageScope scope,
		SessionAddress? session,
		string feature,
		string name,
		JsonElement payload) =>
		new(scope, session, MessageKind.Event, null, feature, name, payload, null);

	public static MessageEnvelope Request(
		MessageScope scope,
		SessionAddress? session,
		string requestId,
		string feature,
		string name,
		JsonElement payload) =>
		new(scope, session, MessageKind.Request, requestId, feature, name, payload, null);

	public static MessageEnvelope Response(
		MessageScope scope,
		SessionAddress? session,
		string requestId,
		string feature,
		string name,
		JsonElement payload,
		string? error) =>
		new(scope, session, MessageKind.Response, requestId, feature, name, payload, error);

	public static MessageEnvelope Cancel(
		MessageScope scope,
		SessionAddress? session,
		string requestId,
		string feature,
		string name) =>
		new(
			scope,
			session,
			MessageKind.Cancel,
			requestId,
			feature,
			name,
			JsonSerializer.SerializeToElement<object?>(null),
			null);

	public static MessageEnvelope SessionEvent(
		SessionAddress session,
		string feature,
		string name,
		JsonElement payload) =>
		Event(MessageScope.Session, session, feature, name, payload);

	public static MessageEnvelope SessionRequest(
		SessionAddress session,
		string requestId,
		string feature,
		string name,
		JsonElement payload) =>
		Request(MessageScope.Session, session, requestId, feature, name, payload);

	public static MessageEnvelope SessionResponse(
		SessionAddress session,
		string requestId,
		string feature,
		string name,
		JsonElement payload,
		string? error) =>
		Response(MessageScope.Session, session, requestId, feature, name, payload, error);

	public static MessageEnvelope SessionCancel(
		SessionAddress session,
		string requestId,
		string feature,
		string name) =>
		Cancel(MessageScope.Session, session, requestId, feature, name);

	public static bool TryParse(string json, out MessageEnvelope? envelope) {
		envelope = null;
		try {
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !TryScope(root, out var scope)
				|| !TryKind(root, out var kind)
				|| !TryRequiredString(root, "feature", out string feature)
				|| !TryRequiredString(root, "name", out string name)
				|| !root.TryGetProperty("payload", out var payload)) {
				return false;
			}

			SessionAddress? session = null;
			if (scope == MessageScope.Session) {
				if (!root.TryGetProperty("session", out var address)
					|| address.ValueKind != JsonValueKind.Object
					|| !TryRequiredString(address, "slot", out string slot)
					|| !TryRequiredString(address, "incarnation", out string incarnation)) {
					return false;
				}

				session = new SessionAddress(slot, incarnation);
			} else if (!root.TryGetProperty("session", out var hostSession)
				|| hostSession.ValueKind != JsonValueKind.Null) {
				return false;
			}

			if (!root.TryGetProperty("requestId", out var request)) {
				return false;
			}

			string? requestId = request.ValueKind == JsonValueKind.String
					? request.GetString()
					: null;
			if (kind == MessageKind.Event
				? request.ValueKind != JsonValueKind.Null
				: string.IsNullOrEmpty(requestId)) {
				return false;
			}

			if (!root.TryGetProperty("error", out var errorElement)
				|| errorElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
				|| kind != MessageKind.Response && errorElement.ValueKind != JsonValueKind.Null) {
				return false;
			}

			string? error = errorElement.ValueKind == JsonValueKind.String
					? errorElement.GetString()
					: null;
			envelope = new MessageEnvelope(
				scope,
				session,
				kind,
				requestId,
				feature,
				name,
				payload.Clone(),
				error);
			return true;
		} catch (JsonException) {
			return false;
		}
	}

	public string ToJson() {
		var wire = new {
			scope = ScopeName(Scope),
			session = Session is null ? null : new {
				slot = Session.Slot,
				incarnation = Session.Incarnation,
			},
			kind = KindName(Kind),
			requestId = RequestId,
			feature = Feature,
			name = Name,
			payload = Payload,
			error = Error,
		};
		return JsonSerializer.Serialize(wire);
	}

	private static string ScopeName(MessageScope scope) => scope switch {
		MessageScope.Host => "host",
		MessageScope.Session => "session",
		_ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown message scope."),
	};

	private static string KindName(MessageKind kind) => kind switch {
		MessageKind.Event => "event",
		MessageKind.Request => "request",
		MessageKind.Response => "response",
		MessageKind.Cancel => "cancel",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown message kind."),
	};

	private static bool TryScope(JsonElement root, out MessageScope scope) {
		scope = default;
		if (!root.TryGetProperty("scope", out var element)
			|| element.ValueKind != JsonValueKind.String) {
			return false;
		}

		switch (element.GetString()) {
			case "host":
				scope = MessageScope.Host;
				return true;
			case "session":
				scope = MessageScope.Session;
				return true;
			default:
				return false;
		}
	}

	private static bool TryKind(JsonElement root, out MessageKind kind) {
		kind = default;
		if (!root.TryGetProperty("kind", out var element)
			|| element.ValueKind != JsonValueKind.String) {
			return false;
		}

		switch (element.GetString()) {
			case "event":
				kind = MessageKind.Event;
				return true;
			case "request":
				kind = MessageKind.Request;
				return true;
			case "response":
				kind = MessageKind.Response;
				return true;
			case "cancel":
				kind = MessageKind.Cancel;
				return true;
			default:
				return false;
		}
	}

	private static bool TryRequiredString(JsonElement root, string property, out string value) {
		value = string.Empty;
		if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) {
			return false;
		}

		value = element.GetString() ?? string.Empty;
		return value.Length > 0;
	}
}
