using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Remote;

/// <summary>
/// A registered remote agent: a friendly <paramref name="Name"/> plus how to reach its runner's control
/// plane (the long-lived daemon on the remote box) — the base <paramref name="Url"/> and bearer
/// <paramref name="Token"/>. The web resolves the actual worker bridge from these on connect. See
/// <c>docs/specs/remote-sessions.md</c>.
/// </summary>
/// <param name="Name">The agent's display name (also its rail/location key); unique within the registry.</param>
/// <param name="Url">The runner control-plane base URL (e.g. <c>http://host:8800</c>).</param>
/// <param name="Token">The runner bearer token.</param>
public readonly record struct RemoteAgent(string Name, string Url, string Token);

/// <summary>
/// The app-global registry of remote agents the user has connected to, persisted to
/// <c>~/.weavie/remote-agents.json</c>. Its own file, never settings.toml — it holds runner bearer tokens, so
/// it must stay off the Claude-facing settings surface. Replaces the web's former <c>localStorage</c> copy,
/// which the Debug dev server's per-launch origin silently orphaned on every restart. <see cref="Add"/>
/// replaces any agent of the same name (matched case-insensitively). Atomic writes; a malformed file is backed
/// up to <c>remote-agents.json.bad</c> and reset rather than throwing.
/// </summary>
public sealed class RemoteAgentStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly List<RemoteAgent> _items;

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/remote-agents.json</c>), loading it now.</summary>
	public RemoteAgentStore(IFileSystem fileSystem, string? path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
		FilePath = path ?? WeaviePaths.RemoteAgentsFile;
		lock (_gate) {
			_items = LoadLocked();
		}
	}

	/// <summary>Raised (off the UI thread) after the registry changes, so each window re-pushes it to its page.</summary>
	public event Action? Changed;

	/// <summary>Diagnostic log line — read failures, malformed-file resets, persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The remote-agents file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>The registered agents, in registration order. Snapshot copy; safe to enumerate.</summary>
	public IReadOnlyList<RemoteAgent> Agents {
		get { lock (_gate) { return [.. _items]; } }
	}

	/// <summary>Registers <paramref name="agent"/>, replacing any existing agent of the same name. No-op for a blank name.</summary>
	public void Add(RemoteAgent agent) {
		if (string.IsNullOrWhiteSpace(agent.Name)) {
			return;
		}

		lock (_gate) {
			_items.RemoveAll(a => NameEquals(a.Name, agent.Name));
			_items.Add(agent);
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>Drops the agent named <paramref name="name"/> (case-insensitive). No-op if absent.</summary>
	public void Remove(string name) {
		bool removed;
		lock (_gate) {
			removed = _items.RemoveAll(a => NameEquals(a.Name, name)) > 0;
			if (removed) {
				PersistLocked();
			}
		}

		if (removed) {
			Changed?.Invoke();
		}
	}

	private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	private List<RemoteAgent> LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return [];
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[remote-agents] could not read {FilePath}: {ex.Message}; starting empty");
			return [];
		}

		try {
			var document = JsonSerializer.Deserialize<Document>(text);
			if (document?.Agents is not { } entries) {
				return [];
			}

			return [.. entries
				.Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Url) && !string.IsNullOrWhiteSpace(e.Token))
				.Select(e => new RemoteAgent(e.Name, e.Url, e.Token))];
		} catch (JsonException ex) {
			Log?.Invoke($"[remote-agents] {FilePath} is malformed ({ex.Message}); backing up to remote-agents.json.bad and resetting");
			JsonStoreFile.BackupBad(_fileSystem, FilePath, text, "remote-agents", Log);
			return [];
		}
	}

	private void PersistLocked() {
		try {
			var document = new Document {
				Version = 1,
				Agents = [.. _items.Select(a => new AgentEntry { Name = a.Name, Url = a.Url, Token = a.Token })],
			};
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(document, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[remote-agents] could not persist: {ex.Message}");
		}
	}

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
