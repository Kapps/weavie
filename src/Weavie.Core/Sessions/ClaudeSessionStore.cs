using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// How the next <c>claude</c> launch in a working directory should be wired: its stable session id (never
/// empty) and whether to <c>--resume</c> or create it fresh with <c>--session-id</c>. The resume flag is
/// derived at launch from whether Claude has a transcript for the id on disk, not stored — see
/// <see cref="ClaudeSessionStore"/>.
/// </summary>
/// <param name="SessionId">The UUID Weavie owns for this working directory's Claude conversation.</param>
/// <param name="Resume">True to reattach (<c>--resume</c>); false to create it (<c>--session-id</c>).</param>
public readonly record struct ClaudeLaunch(string SessionId, bool Resume);

/// <summary>
/// Remembers the Claude Code session id Weavie assigned each working directory (keyed by launch directory),
/// persisted atomically to <c>~/.weavie/claude-sessions.json</c>, so reopening resumes the previous
/// conversation. Weavie assigns the id as <c>--session-id</c> rather than scraping Claude's storage, so
/// resume is deterministic. Whether a launch resumes or re-creates the id is not tracked here — it is decided
/// from whether Claude's transcript for the id exists on disk (<see cref="IClaudeTranscripts"/>), the same
/// thing <c>claude</c> itself checks, so the two can never drift apart (a stored "started" bit could, and a
/// stale one made a relaunch re-create an id whose conversation still existed → "Session ID … is already in
/// use"). A malformed file is backed up to <c>claude-sessions.json.bad</c> and reset.
/// </summary>
public sealed class ClaudeSessionStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly List<Entry> _items;

	/// <summary>Creates the store over <paramref name="path"/>, loading it now.</summary>
	public ClaudeSessionStore(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
		lock (_gate) {
			_items = LoadLocked();
		}
	}

	/// <summary>Diagnostic log line — read failures, malformed-file resets, persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>
	/// Returns the stable Claude session id for <paramref name="workingDirectory"/>, minting and persisting one
	/// on first use so it is known up front and resume is deterministic. Whether the next launch <c>--resume</c>s
	/// or re-creates this id is decided at launch from the transcript on disk, not here.
	/// </summary>
	public string Resolve(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			var entry = Find(key);
			if (entry is null) {
				entry = new Entry { Key = key, Id = Guid.NewGuid().ToString() };
				_items.Add(entry);
				PersistLocked();
			}

			return entry.Id;
		}
	}

	/// <summary>
	/// Abandons <paramref name="workingDirectory"/>'s assigned id entirely (next <see cref="Resolve"/>
	/// cold-starts a new one). Used when a launch could not bring the id up at all — its transcript is pruned or
	/// corrupt, or the id is otherwise poison — so there is nothing to preserve.
	/// </summary>
	public void Forget(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			if (RemoveLocked(key)) {
				PersistLocked();
			}
		}
	}

	/// <summary>
	/// Drops <paramref name="workingDirectory"/>'s tracked id on a user <c>/clear</c>, so a relaunch cold-starts
	/// instead of reattaching to the stale transcript the clear meant to escape; the next real user message
	/// re-establishes tracking via <see cref="Adopt"/>.
	/// </summary>
	public void Clear(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			if (RemoveLocked(key)) {
				PersistLocked();
			}
		}
	}

	/// <summary>
	/// Records the session id claude reports it's actually in for <paramref name="workingDirectory"/> (observed
	/// off the hook stream on a real user message), realigning the store after claude rotated its id out from
	/// under Weavie (chiefly a <c>/clear</c>). A no-op when the id already matches, so the normal flow never
	/// thrashes the file.
	/// </summary>
	public void Adopt(string workingDirectory, string sessionId) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			var entry = Find(key);
			if (entry is null) {
				_items.Add(new Entry { Key = key, Id = sessionId });
				PersistLocked();
				return;
			}

			if (!string.Equals(entry.Id, sessionId, StringComparison.Ordinal)) {
				entry.Id = sessionId;
				PersistLocked();
			}
		}
	}

	private Entry? Find(string key) => _items.FirstOrDefault(e => PathEquals(e.Key, key));

	private bool RemoveLocked(string key) => _items.RemoveAll(e => PathEquals(e.Key, key)) > 0;

	private static string Normalize(string path) =>
		Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

	private static bool PathEquals(string a, string b) =>
		string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

	private List<Entry> LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return [];
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[claude-sessions] could not read {FilePath}: {ex.Message}; starting empty");
			return [];
		}

		try {
			var document = JsonSerializer.Deserialize<Document>(text);
			if (document?.Sessions is not { } entries) {
				return [];
			}

			return [.. entries
				.Where(e => !string.IsNullOrWhiteSpace(e.Cwd) && !string.IsNullOrWhiteSpace(e.Id))
				.Select(e => new Entry { Key = e.Cwd, Id = e.Id })];
		} catch (JsonException ex) {
			Log?.Invoke($"[claude-sessions] {FilePath} is malformed ({ex.Message}); backing up to claude-sessions.json.bad and resetting");
			JsonStoreFile.BackupBad(_fileSystem, FilePath, text, "claude-sessions", Log);
			return [];
		}
	}

	private void PersistLocked() {
		try {
			var document = new Document {
				Version = 1,
				Sessions = [.. _items.Select(e => new SessionEntry { Cwd = e.Key, Id = e.Id })],
			};
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(document, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[claude-sessions] could not persist: {ex.Message}");
		}
	}

	private sealed class Entry {
		public required string Key { get; init; }
		public required string Id { get; set; }
	}

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("sessions")]
		public List<SessionEntry> Sessions { get; set; } = [];
	}

	private sealed class SessionEntry {
		[JsonPropertyName("cwd")]
		public string Cwd { get; set; } = string.Empty;

		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;
	}
}
