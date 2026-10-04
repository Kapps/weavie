using System.Text.Json;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private object RequestInput(AcpClientRequest request, AcpClientRequestState state) {
		string mode = RequiredString(request.Parameters, "mode", "elicitation request");
		if (mode == "url") {
			string elicitationId = RequiredString(request.Parameters, "elicitationId", "URL elicitation");
			string url = AcpElicitationSchema.RequireHttpUrl(RequiredString(request.Parameters, "url", "URL elicitation"));
			if (!_urlElicitations.TryAdd(elicitationId, request.Id)) {
				throw new AcpProtocolException($"ACP repeated outstanding URL elicitation id '{elicitationId}'.");
			}
			var data = JsonSerializer.SerializeToElement(Array.Empty<object>());
			var urlPending = new AcpPendingRequest(request, "url", data, SessionId(), TurnId());
			if (!_pendingRequests.TryAdd(request.Id, urlPending)) {
				_urlElicitations.TryRemove(elicitationId, out _);
				throw new AcpProtocolException($"ACP request id '{request.Id}' is already pending.");
			}
			try {
				PublishInputRequest(state, urlPending, () => new AgentPaneMessage {
					Type = "input-requested",
					ProviderId = _definition.Id,
					ItemId = $"request:{request.Id}",
					RequestId = request.Id,
					ItemType = "url",
					Summary = OptionalString(request.Parameters, "message") ?? "Open this link to continue",
					ResourceUri = url,
					Actions = [new AgentActionOption { Id = "accept", Label = "Open link", Kind = "open_url" }],
					Status = "pending",
				});
			} catch {
				_urlElicitations.TryRemove(elicitationId, out _);
				throw;
			}
			return DeferredClientResponse;
		}
		if (mode != "form" || !request.Parameters.TryGetProperty("requestedSchema", out var schema)) {
			throw new AcpProtocolException($"Unsupported ACP elicitation mode '{mode}'.");
		}
		var questions = AcpElicitationSchema.ReadQuestions(schema, OptionalString(request.Parameters, "message"));
		var pending = new AcpPendingRequest(request, "input", schema.Clone(), SessionId(), TurnId());
		if (!_pendingRequests.TryAdd(request.Id, pending)) {
			throw new AcpProtocolException($"ACP request id '{request.Id}' is already pending.");
		}
		PublishInputRequest(state, pending, () => new AgentPaneMessage {
			Type = "input-requested",
			ProviderId = _definition.Id,
			ItemId = $"request:{request.Id}",
			RequestId = request.Id,
			ItemType = "elicitation",
			Summary = OptionalString(request.Parameters, "message") ?? "Input requested",
			Questions = questions,
			Status = "pending",
		});
		return DeferredClientResponse;
	}

	private void PublishInputRequest(
		AcpClientRequestState state,
		AcpPendingRequest pending,
		Func<AgentPaneMessage> createMessage) {
		// The transition gate precedes the request lock, as it does for a concurrent $/cancel_request.
		lock (_turnTransitionGate) {
			if (state.PublishDeferred(() => {
				Observe(new AgentInputRequested());
				Observe(new AgentInputResolved(RequiresUserInput: true));
				// The pane keys an item by (threadId, turnId, itemId), and the resolution reads its identity off this
				// same record -- so stamping it here is what keeps the two from ever disagreeing.
				Emit(createMessage() with { ThreadId = pending.ThreadId, TurnId = pending.TurnId });
			})) return;
		}
		_pendingRequests.TryRemove(pending.Request.Id, out _);
		state.Token.ThrowIfCancellationRequested();
	}
}
