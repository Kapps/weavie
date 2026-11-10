using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Credentials;

/// <summary>
/// A secret Weavie holds (an API key or access token). It prints as <c>[redacted]</c> and refuses JSON serialization,
/// so a leak fails loudly instead of landing in a log, toast, receipt, or payload; <see cref="Reveal"/> is the only way out.
/// </summary>
[JsonConverter(typeof(RefusingConverter))]
public sealed class CredentialSecret {
	private readonly string _value;

	/// <summary>Wraps <paramref name="value"/>, which must not be empty or whitespace.</summary>
	public CredentialSecret(string value) {
		ArgumentException.ThrowIfNullOrWhiteSpace(value);
		_value = value;
	}

	/// <summary>The plain secret; call it only where a request's authorization is set.</summary>
	public string Reveal() => _value;

	/// <inheritdoc/>
	public override string ToString() => "[redacted]";

	/// <summary>Throws on every read and write, so no serializer can carry a secret.</summary>
	public sealed class RefusingConverter : JsonConverter<CredentialSecret> {
		/// <inheritdoc/>
		public override CredentialSecret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			throw new NotSupportedException("A credential secret is never deserialized.");

		/// <inheritdoc/>
		public override void Write(Utf8JsonWriter writer, CredentialSecret value, JsonSerializerOptions options) =>
			throw new NotSupportedException("A credential secret is never serialized.");
	}
}
