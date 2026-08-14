using System.Text.Json;

namespace Weavie.AgentClientProtocol;

/// <summary>One agent-to-client JSON-RPC request.</summary>
public sealed record AcpClientRequest(
	string Id,
	JsonElement ResponseId,
	string Method,
	JsonElement Parameters,
	long Generation);

/// <summary>Identity for one supervised agent process generation.</summary>
public readonly record struct AcpProcessGeneration(long Generation, int Attempt);

/// <summary>A JSON-RPC error returned by an ACP agent.</summary>
public sealed class AcpRequestException : InvalidOperationException {
	private AcpRequestException(int code, string message, JsonElement? data) : base(message) {
		Code = code;
		DataPayload = data;
	}

	/// <summary>The JSON-RPC error code.</summary>
	public int Code { get; }

	/// <summary>The optional JSON-RPC error data.</summary>
	public JsonElement? DataPayload { get; }

	internal static AcpRequestException From(JsonElement error) {
		if (error.ValueKind != JsonValueKind.Object
			|| !error.TryGetProperty("code", out var codeValue)
			|| !codeValue.TryGetInt32(out int code)
			|| !error.TryGetProperty("message", out var messageValue)
			|| messageValue.ValueKind != JsonValueKind.String) {
			throw new AcpProtocolException("ACP response errors require an integer code and string message.");
		}
		return new AcpRequestException(
			code,
			messageValue.GetString() ?? string.Empty,
			error.TryGetProperty("data", out var data) ? data.Clone() : null);
	}
}

/// <summary>A strict ACP wire or lifecycle invariant failure.</summary>
public sealed class AcpProtocolException : InvalidOperationException {
	/// <summary>Creates a protocol failure.</summary>
	public AcpProtocolException(string message) : base(message) {
	}

	/// <summary>Creates a protocol failure with its underlying cause.</summary>
	public AcpProtocolException(string message, Exception innerException) : base(message, innerException) {
	}
}
