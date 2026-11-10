using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Weavie.Core.Inference;

/// <summary>A strict typed-response contract: the decoder and the JSON schema the provider must satisfy.</summary>
/// <typeparam name="T">The decoded response type.</typeparam>
public sealed class InferenceResponseType<T> {
	/// <summary>Pairs strict metadata with an explicit schema, for shapes the type system can't describe.</summary>
	public InferenceResponseType(JsonTypeInfo<T> typeInfo, string schemaJson) {
		ArgumentNullException.ThrowIfNull(typeInfo);
		ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
		if (typeInfo.Options.UnmappedMemberHandling != JsonUnmappedMemberHandling.Disallow
			|| !typeInfo.Options.RespectRequiredConstructorParameters) {
			throw new InvalidOperationException(
				"Inference response metadata must disallow unmapped members and respect required constructor parameters.");
		}
		TypeInfo = typeInfo;
		SchemaJson = schemaJson;
	}

	/// <summary>The strict decoder.</summary>
	public JsonTypeInfo<T> TypeInfo { get; }

	/// <summary>The output schema sent to the provider.</summary>
	public string SchemaJson { get; }
}

/// <summary>Builds response contracts whose schema is exported from the type metadata.</summary>
public static class InferenceResponseType {
	private static readonly JsonSchemaExporterOptions SchemaOptions = new() {
		TreatNullObliviousAsNonNullable = true,
	};

	/// <summary>A contract whose schema is exported from <paramref name="typeInfo"/>.</summary>
	public static InferenceResponseType<T> Of<T>(JsonTypeInfo<T> typeInfo) {
		ArgumentNullException.ThrowIfNull(typeInfo);
		return new(typeInfo, JsonSchemaExporter.GetJsonSchemaAsNode(typeInfo, SchemaOptions).ToJsonString());
	}
}
