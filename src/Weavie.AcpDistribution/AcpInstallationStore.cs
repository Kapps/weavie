using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.AcpDistribution;

internal sealed class AcpInstallationStore {
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
		PropertyNameCaseInsensitive = false,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};
	private readonly IFileSystem _fileSystem;
	private readonly string _path;

	public AcpInstallationStore(IFileSystem fileSystem, string path) {
		_fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = Path.GetFullPath(path);
	}

	public IReadOnlyList<AcpLaunchSpec> Load() {
		if (!_fileSystem.FileExists(_path)) return [];
		var document = JsonSerializer.Deserialize<Document>(_fileSystem.ReadAllText(_path), JsonOptions)
			?? throw new JsonException("The ACP installation document is empty.");
		if (document.Version != 1) throw new JsonException("The ACP installation document requires version 1.");
		if (document.Agents is null) throw new JsonException("The ACP installation document requires an agents array.");
		var ids = new HashSet<string>(StringComparer.Ordinal);
		return [.. document.Agents.Select(entry => Validate(
			entry ?? throw new JsonException("ACP installations cannot contain null entries."), ids))];
	}

	public void Save(IReadOnlyList<AcpLaunchSpec> agents) {
		ArgumentNullException.ThrowIfNull(agents);
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var validated = agents.Select(agent => Validate(agent, ids)).ToArray();
		_fileSystem.WriteAllTextAtomic(_path, JsonSerializer.Serialize(new Document {
			Version = 1,
			Agents = validated,
		}, JsonOptions));
	}

	private static AcpLaunchSpec Validate(AcpLaunchSpec agent, HashSet<string> ids) {
		ArgumentNullException.ThrowIfNull(agent);
		if (string.IsNullOrWhiteSpace(agent.Id) || !ids.Add(agent.Id)) {
			throw new JsonException("Every ACP installation requires a unique non-empty id.");
		}
		if (string.IsNullOrWhiteSpace(agent.Name) || string.IsNullOrWhiteSpace(agent.Command)
			|| string.IsNullOrWhiteSpace(agent.Version) || string.IsNullOrWhiteSpace(agent.Distribution)) {
			throw new JsonException($"ACP installation '{agent.Id}' is incomplete.");
		}
		if (agent.Arguments is null || agent.Arguments.Any(value => value is null)
			|| agent.Environment is null || agent.Environment.Any(entry => entry.Value is null)) {
			throw new JsonException($"ACP installation '{agent.Id}' has malformed launch data.");
		}
		try {
			ValidateLaunch(agent);
		} catch (InvalidDataException ex) {
			throw new JsonException($"ACP installation '{agent.Id}' has invalid launch data.", ex);
		}
		return agent with {
			Arguments = [.. agent.Arguments],
			Environment = new Dictionary<string, string>(agent.Environment, StringComparer.Ordinal),
		};
	}

	private static void ValidateLaunch(AcpLaunchSpec agent) {
		switch (agent.Distribution) {
			case "binary" when !Path.IsPathFullyQualified(agent.Command):
				throw new InvalidDataException("A binary installation requires an absolute command.");
			case "binary":
				return;
			case "npx" when agent.Command != "npx" || agent.Arguments.FirstOrDefault() != "--yes":
				throw new InvalidDataException("An npx installation requires the exact npx runner and --yes.");
			case "npx":
				AcpDistributionService.PackageProcess("npx", agent.Arguments, OperatingSystem.IsWindows());
				return;
			case "uvx" when agent.Command == "uvx":
				return;
			default:
				throw new InvalidDataException($"Unknown ACP distribution '{agent.Distribution}'.");
		}
	}

	private sealed record Document {
		[JsonPropertyName("version")]
		public int Version { get; init; }

		[JsonPropertyName("agents")]
		public AcpLaunchSpec?[]? Agents { get; init; }
	}
}
