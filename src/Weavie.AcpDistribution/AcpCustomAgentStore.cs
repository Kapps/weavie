using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.AcpDistribution;

internal sealed class AcpCustomAgentStore {
	private static readonly JsonSerializerOptions JsonOptions = new() {
		PropertyNameCaseInsensitive = false,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};
	private readonly IFileSystem _fileSystem;
	private readonly string _path;

	public AcpCustomAgentStore(IFileSystem fileSystem, string path) {
		_fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = Path.GetFullPath(path);
	}

	public IReadOnlyList<AcpLaunchSpec> Load() {
		if (!_fileSystem.FileExists(_path)) return [];
		var document = JsonSerializer.Deserialize<Document>(_fileSystem.ReadAllText(_path), JsonOptions)
			?? throw new JsonException("The custom ACP agent document is empty.");
		if (document.Version != 1) throw new JsonException("The custom ACP agent document requires version 1.");
		if (document.Agents is null) throw new JsonException("The custom ACP agent document requires an agents array.");
		var ids = new HashSet<string>(StringComparer.Ordinal);
		return [.. document.Agents.Select(entry => Build(
			entry ?? throw new JsonException("Custom ACP agents cannot contain null entries."), ids))];
	}

	private static AcpLaunchSpec Build(Profile profile, HashSet<string> ids) {
		string id = Require(profile.Id, "id");
		if (!ids.Add(id)) throw new JsonException($"Custom ACP agent '{id}' is repeated.");
		string name = Require(profile.Name, $"agent '{id}' name");
		string command = Require(profile.Command, $"agent '{id}' command");
		if (profile.Arguments?.Any(value => value is null) == true
			|| profile.Environment?.Any(entry => entry.Value is null) == true) {
			throw new JsonException($"Custom ACP agent '{id}' has malformed launch data.");
		}
		return new AcpLaunchSpec {
			Id = id,
			Name = name,
			Version = null,
			Command = command,
			Arguments = profile.Arguments is null ? [] : [.. profile.Arguments!],
			Environment = profile.Environment is null
				? new Dictionary<string, string>(StringComparer.Ordinal)
				: profile.Environment.ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal),
			Distribution = "custom",
		};
	}

	private static string Require(string? value, string field) =>
		!string.IsNullOrWhiteSpace(value)
			? value
			: throw new JsonException($"The custom ACP {field} is missing.");

	private sealed record Document {
		[JsonPropertyName("version")]
		public int Version { get; init; }

		[JsonPropertyName("agents")]
		public Profile?[]? Agents { get; init; }
	}

	private sealed record Profile {
		[JsonPropertyName("id")]
		public string? Id { get; init; }

		[JsonPropertyName("name")]
		public string? Name { get; init; }

		[JsonPropertyName("command")]
		public string? Command { get; init; }

		[JsonPropertyName("args")]
		public string?[]? Arguments { get; init; }

		[JsonPropertyName("env")]
		public Dictionary<string, string?>? Environment { get; init; }
	}
}
