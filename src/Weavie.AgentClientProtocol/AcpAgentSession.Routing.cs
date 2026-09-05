using System.Text.Json;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private void RouteNotification(long generation, JsonElement root) {
		lock (_turnTransitionGate) {
			if (!OwnsGeneration(generation)) return;
			if (root.TryGetProperty("params", out var parameters)
				&& parameters.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String) {
				lock (_gate) if (_closedSideSessionIds.Contains(id.GetString()!)) return;
				var owner = SessionOwner(id.GetString()!);
				try {
					owner.HandleNotification(generation, root);
				} catch (Exception error) when (!ReferenceEquals(owner, this)) {
					owner.FailRuntime(error);
				}
				return;
			}
			if (OptionalString(root, "method") == "$/cancel_request") {
				foreach (var side in SideRuntimes()) side.Session.HandleNotification(generation, root);
			}
			HandleNotification(generation, root);
		}
	}

	private void RouteClientRequest(AcpClientRequest request) {
		lock (_turnTransitionGate) {
			if (!OwnsGeneration(request.Generation)) return;
			string? sessionId = OptionalString(request.Parameters, "sessionId");
			if (sessionId is null && request.Parameters.TryGetProperty("requestId", out var requestId)) {
				if (_connection.TryGetRequestSession(requestId, out sessionId) && sessionId is null) {
					RegisterClientRequest(request);
					return;
				}
			}
			if (sessionId is null) {
				throw new AcpProtocolException($"ACP request '{request.Method}' has no known conversation owner.");
			}
			lock (_gate) {
				if (_closedSideSessionIds.Contains(sessionId)) {
					Run(() => _connection.RespondErrorAsync(request, -32602, "The conversation is closed.", null));
					return;
				}
			}
			SessionOwner(sessionId).RegisterClientRequest(request);
		}
	}

	private AcpAgentSession SessionOwner(string sessionId) {
		lock (_gate) {
			foreach (var side in _sideRuntimes.Values) {
				if (side.Conversation.ProviderSessionId == sessionId) return side.Session;
			}
			if (_sessionId == sessionId || _openingSessionId == sessionId
				|| _sessionOpening && _sessionId is null && _openingSessionId is null) return this;
		}
		throw new AcpProtocolException($"ACP addressed an unknown conversation '{sessionId}'.");
	}

	private SideRuntime[] SideRuntimes() {
		lock (_gate) return [.. _sideRuntimes.Values];
	}

	private void FailSideRuntimes(Exception error) {
		foreach (var side in SideRuntimes()) side.Session.FailRuntime(error);
	}
}
