using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Remote;

/// <summary>
/// A registered remote agent: a friendly <paramref name="Name"/> plus how to reach its runner control plane
/// (<paramref name="Url"/> + bearer <paramref name="Token"/>). See <c>docs/specs/remote-sessions.md</c>.
/// </summary>
/// <param name="Name">The agent's display name (also its rail/location key); unique within the registry.</param>
/// <param name="Url">The runner control-plane base URL (e.g. <c>http://host:8800</c>).</param>
/// <param name="Token">The runner bearer token.</param>
public readonly record struct RemoteAgent(string Name, string Url, string Token);

/// <summary>
/// The app-global registry of connected remote agents, persisted atomically to
/// <c>~/.weavie/remote-agents.json</c>. Its own file, never settings.toml — it holds runner bearer tokens, so
/// it must stay off the Claude-facing settings surface. <see cref="Add"/> replaces any same-named agent
/// (case-insensitive); a malformed file is backed up to <c>remote-agents.json.bad</c> and reset, not thrown.
/// </summary>
public sealed class RemoteAgentStore : JsonDocumentStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private List<RemoteAgent> _items = [];

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/remote-agents.json</c>), loading it now.</summary>
	/// <param name="fileSystem">The filesystem the registry persists through.</param>
	/// <param name="path">The backing file, or <c>null</c> for the default.</param>
	public RemoteAgentStore(IFileSystem fileSystem, string? path)
		: base(fileSystem, path ?? WeaviePaths.RemoteAgentsFile) {
		Load();
	}

	/// <summary>Raised (off the UI thread) after the registry changes, so each window re-pushes it to its page.</summary>
	public event Action? Changed;

	/// <summary>The registered agents, in registration order. Snapshot copy; safe to enumerate.</summary>
	public IReadOnlyList<RemoteAgent> Agents {
		get { lock (Gate) { return [.. _items]; } }
	}

	/// <summary>Registers <paramref name="agent"/>, replacing any existing agent of the same name. No-op for a blank name.</summary>
	public void Add(RemoteAgent agent) {
		if (string.IsNullOrWhiteSpace(agent.Name)) {
			return;
		}

		lock (Gate) {
			_items.RemoveAll(a => NameEquals(a.Name, agent.Name));
			_items.Add(agent);
			PersistRestricted();
		}

		Changed?.Invoke();
	}

	/// <summary>Drops the agent named <paramref name="name"/> (case-insensitive). No-op if absent.</summary>
	public void Remove(string name) {
		bool removed;
		lock (Gate) {
			removed = _items.RemoveAll(a => NameEquals(a.Name, name)) > 0;
			if (removed) {
				PersistRestricted();
			}
		}

		if (removed) {
			Changed?.Invoke();
		}
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		var document = text is null ? null : JsonSerializer.Deserialize<Document>(text);
		_items = document?.Agents is not { } entries
			? []
			: [.. entries
				.Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Url) && !string.IsNullOrWhiteSpace(e.Token))
				.Select(e => new RemoteAgent(e.Name, e.Url, e.Token))];
	}

	/// <inheritdoc/>
	protected override string Render() => JsonSerializer.Serialize(
		new Document {
			Version = 1,
			Agents = [.. _items.Select(a => new AgentEntry { Name = a.Name, Url = a.Url, Token = a.Token })],
		},
		JsonOptions);

	private void PersistRestricted() => PersistLocked(() => SecureFile.Restrict(FilePath));

	private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("agents")]
		public List<AgentEntry> Agents { get; set; } = [];
	}

	private sealed class AgentEntry {
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		[JsonPropertyName("url")]
		public string Url { get; set; } = string.Empty;

		[JsonPropertyName("token")]
		public string Token { get; set; } = string.Empty;
	}
}
