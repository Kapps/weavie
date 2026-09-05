using System.Text.Json;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpJsonRpcConnection {
	private Action? HandleLine(string line, long generation) {
		using var document = JsonDocument.Parse(line);
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("jsonrpc", out var version)
			|| version.ValueKind != JsonValueKind.String
			|| version.GetString() != "2.0") {
			throw new JsonException("ACP stdout must contain JSON-RPC 2.0 objects.");
		}

		if (root.TryGetProperty("id", out var responseId)) {
			if (root.TryGetProperty("method", out var requestMethod)) {
				if (requestMethod.ValueKind != JsonValueKind.String
					|| string.IsNullOrEmpty(requestMethod.GetString())) {
					throw new AcpProtocolException("ACP requests must have a non-empty string method.");
				}
				if (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)) {
					throw new AcpProtocolException("ACP requests cannot contain result or error.");
				}
				if (responseId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) {
					throw new AcpProtocolException("ACP request ids must be strings or numbers.");
				}
				if (root.TryGetProperty("params", out var requestParameters)
					&& requestParameters.ValueKind != JsonValueKind.Object) {
					throw new AcpProtocolException("ACP request params must be an object.");
				}
				var request = new AcpClientRequest(
					CanonicalId(responseId),
					responseId.Clone(),
					requestMethod.GetString() ?? string.Empty,
					root.TryGetProperty("params", out requestParameters) ? requestParameters.Clone() : EmptyObject(),
					generation);
				return () => DispatchRequest(request);
			}

			bool hasError = root.TryGetProperty("error", out var error);
			bool hasResult = root.TryGetProperty("result", out var result);
			if (hasError == hasResult) {
				throw new AcpProtocolException("ACP responses must contain exactly one of result or error.");
			}
			if (hasError && error.ValueKind != JsonValueKind.Object) {
				throw new AcpProtocolException("ACP response errors must be objects.");
			}
			if (responseId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null)) {
				throw new AcpProtocolException("ACP response ids must be strings, numbers, or null.");
			}
			if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt64(out long id)) {
				throw new AcpProtocolException("ACP responses must use the numeric id assigned by Weavie.");
			}
			var requestError = hasError ? AcpRequestException.From(error) : null;
			if (_pending.TryGetValue(id, out var pending)) {
				if (pending.Generation != generation) {
					throw new AcpProtocolException($"ACP response {id} belongs to another process generation.");
				}
				if (requestError is null && pending.Binds is { } binds) {
					if (!result.TryGetProperty("sessionId", out var boundId) || boundId.ValueKind != JsonValueKind.String) {
						throw new AcpProtocolException("ACP session creation returned no sessionId.");
					}
					binds.Bind(boundId.GetString()!);
				}
				_pending.TryRemove(id, out _);
				if (requestError is not null) pending.Completion.TrySetException(requestError);
				else pending.Completion.TrySetResult(result.Clone());
				return null;
			}
			if (_cancelled.TryRemove(id, out _)) {
				return null;
			}
			throw new AcpProtocolException($"ACP returned an unsolicited response id {id}.");
		}

		if (!root.TryGetProperty("method", out var method)
			|| method.ValueKind != JsonValueKind.String
			|| string.IsNullOrEmpty(method.GetString())) {
			throw new JsonException("ACP notification is missing a method.");
		}
		if (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)) {
			throw new AcpProtocolException("ACP notifications cannot contain result or error.");
		}
		if (root.TryGetProperty("params", out var notificationParameters)
			&& notificationParameters.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("ACP notification params must be an object.");
		}
		var notification = root.Clone();
		return () => DispatchNotification(generation, notification);
	}

}
