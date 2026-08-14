using System.Text.Json.Serialization;

namespace Weavie.AcpDistribution;

internal sealed record AcpRegistryDocument {
	[JsonPropertyName("version")]
	public string? Version { get; init; }

	[JsonPropertyName("agents")]
	public AcpRegistryEntry?[]? Agents { get; init; }
}

internal sealed record AcpRegistryEntry {
	[JsonPropertyName("id")]
	public string? Id { get; init; }

	[JsonPropertyName("name")]
	public string? Name { get; init; }

	[JsonPropertyName("version")]
	public string? Version { get; init; }

	[JsonPropertyName("description")]
	public string? Description { get; init; }

	[JsonPropertyName("distribution")]
	public AcpRegistryDistribution? Distribution { get; init; }
}

internal sealed record AcpRegistryDistribution {
	[JsonPropertyName("binary")]
	public Dictionary<string, AcpBinaryDistribution?>? Binary { get; init; }

	[JsonPropertyName("npx")]
	public AcpPackageDistribution? Npx { get; init; }

	[JsonPropertyName("uvx")]
	public AcpPackageDistribution? Uvx { get; init; }
}

internal sealed record AcpPackageDistribution {
	[JsonPropertyName("package")]
	public string? Package { get; init; }

	[JsonPropertyName("args")]
	public string?[]? Arguments { get; init; }

	[JsonPropertyName("env")]
	public Dictionary<string, string?>? Environment { get; init; }
}

internal sealed record AcpBinaryDistribution {
	[JsonPropertyName("archive")]
	public string? Archive { get; init; }

	[JsonPropertyName("sha256")]
	public string? Sha256 { get; init; }

	[JsonPropertyName("cmd")]
	public string? Command { get; init; }

	[JsonPropertyName("args")]
	public string?[]? Arguments { get; init; }

	[JsonPropertyName("env")]
	public Dictionary<string, string?>? Environment { get; init; }
}
