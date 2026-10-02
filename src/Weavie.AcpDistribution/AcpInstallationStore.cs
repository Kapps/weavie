using System.Text.Json;
using System.Text.Json.Nodes;
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

	public AcpInstallations Load() {
		if (!_fileSystem.FileExists(_path)) return AcpInstallations.Empty;
		var document = JsonSerializer.Deserialize<Document>(_fileSystem.ReadAllText(_path), JsonOptions)
			?? throw new JsonException("The ACP installation document is empty.");
		if (document.Version != 1) throw new JsonException("The ACP installation document requires version 1.");
		if (document.Agents is null) throw new JsonException("The ACP installation document requires an agents array.");
		var agents = new List<AcpLaunchSpec>();
		var broken = new List<AcpBrokenInstallation>();
		foreach (var entry in document.Agents) {
			if (entry.ValueKind != JsonValueKind.Object) throw new JsonException("ACP installations must be objects.");
			try {
				agents.Add(Validate(entry.Deserialize<AcpLaunchSpec>(JsonOptions)
					?? throw new JsonException("ACP installations cannot contain null entries.")));
			} catch (JsonException ex) {
				broken.Add(Broken(entry, ex));
			}
		}
		var installations = new AcpInstallations(agents, broken);
		RequireUniqueIds(installations);
		return installations;
	}

	public void Save(AcpInstallations installations) {
		ArgumentNullException.ThrowIfNull(installations);
		RequireUniqueIds(installations);
		var agents = new JsonArray([
			.. installations.Agents.Select(agent => JsonSerializer.SerializeToNode(Validate(agent), JsonOptions)),
			.. installations.Broken.Select(entry => JsonNode.Parse(entry.Recorded.GetRawText())),
		]);
		_fileSystem.WriteAllTextAtomic(_path, new JsonObject { ["version"] = 1, ["agents"] = agents }.ToJsonString());
	}

	// An entry this build can't launch stays recorded verbatim, addressable by its id for reinstall or removal.
	private static AcpBrokenInstallation Broken(JsonElement entry, JsonException failure) {
		string id = Text(entry, "id") ?? throw new JsonException("Every ACP installation requires a non-empty id.", failure);
		string reason = (failure.InnerException as InvalidDataException)?.Message ?? failure.Message;
		return new AcpBrokenInstallation(new AcpBrokenAgent {
			Id = id,
			Name = Text(entry, "name") ?? id,
			Distribution = Text(entry, "distribution"),
			Reason = reason,
		}, entry.Clone());
	}

	private static string? Text(JsonElement entry, string property) =>
		entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(value.GetString())
			? value.GetString()
			: null;

	private static void RequireUniqueIds(AcpInstallations installations) {
		var ids = new HashSet<string>(StringComparer.Ordinal);
		if (!installations.Ids.All(ids.Add)) throw new JsonException("Every ACP installation requires a unique id.");
	}

	private static AcpLaunchSpec Validate(AcpLaunchSpec agent) {
		ArgumentNullException.ThrowIfNull(agent);
		if (string.IsNullOrWhiteSpace(agent.Id)) throw new JsonException("Every ACP installation requires a non-empty id.");
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
			case "npx" when !Path.IsPathFullyQualified(agent.Command) && !agent.Arguments.Any(Path.IsPathFullyQualified):
				throw new InvalidDataException("This npx install is from an older Weavie and can't be launched.");
			case "npx":
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
		public JsonElement[]? Agents { get; init; }
	}
}

/// <summary>The recorded registry installations: launchable recipes and entries this build can't launch.</summary>
internal sealed record AcpInstallations(IReadOnlyList<AcpLaunchSpec> Agents, IReadOnlyList<AcpBrokenInstallation> Broken) {
	public static AcpInstallations Empty { get; } = new([], []);

	public IEnumerable<string> Ids => Agents.Select(agent => agent.Id).Concat(Broken.Select(entry => entry.Agent.Id));

	public AcpInstallations Without(string id) => new(
		[.. Agents.Where(agent => agent.Id != id)],
		[.. Broken.Where(entry => entry.Agent.Id != id)]);
}

/// <summary>An unlaunchable installation and its exact recorded JSON.</summary>
internal sealed record AcpBrokenInstallation(AcpBrokenAgent Agent, JsonElement Recorded);
