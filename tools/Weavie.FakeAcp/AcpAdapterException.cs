using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

/// <summary>A JSON-RPC failure intentionally returned to an ACP client.</summary>
public sealed class AcpAdapterException : InvalidOperationException {
	/// <summary>Creates an ACP request failure.</summary>
	public AcpAdapterException(int code, string message, JsonNode? data) : base(message) {
		Code = code;
		DataPayload = data;
	}

	/// <summary>The JSON-RPC error code.</summary>
	public int Code { get; }

	/// <summary>Structured error details.</summary>
	public JsonNode? DataPayload { get; }

	/// <summary>Creates an invalid-params response.</summary>
	public static AcpAdapterException InvalidParams(string message) => new(-32602, message, null);

	/// <summary>Creates a missing-resource response.</summary>
	public static AcpAdapterException ResourceNotFound(string message) => new(-32002, message, null);

	/// <summary>Creates an authentication-required response.</summary>
	public static AcpAdapterException AuthenticationRequired(string message) => new(-32000, message, null);
}
