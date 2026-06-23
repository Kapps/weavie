using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// How the next <c>claude</c> launch in a working directory should be wired: its stable session id (never
/// empty) and whether to <c>--resume</c> or create it fresh with <c>--session-id</c>.
/// </summary>
/// <param name="SessionId">The UUID Weavie owns for this working directory's Claude conversation.</param>
/// <param name="Resume">True to reattach (<c>--resume</c>); false to create it (<c>--session-id</c>).</param>
public readonly record struct ClaudeLaunch(string SessionId, bool Resume);

/// <summary>
/// Remembers the Claude Code session id Weavie assigned each working directory (keyed by launch directory),
/// persisted atomically to <c>~/.weavie/claude-sessions.json</c>, so reopening resumes the previous
/// conversation. Weavie assigns the id as <c>--session-id</c> rather than scraping Claude's storage, so
/// resume is deterministic. A malformed file is backed up to <c>claude-sessions.json.bad</c> and reset.
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
	/// Returns how to launch <c>claude</c> in <paramref name="workingDirectory"/>: mints + persists a stable
	/// session id the first time (Resume false ⇒ <c>--session-id</c>), then reattaches only once the session is
	/// <see cref="Adopt">adopted</see> (a real user message — when claude actually writes its transcript), else
	/// re-creates under the same id. Output volume alone never marks a session resumable, so a launch that comes
	/// up but is never messaged isn't mistaken for one with a transcript. Persists only when it mints.
	/// </summary>
	public ClaudeLaunch Resolve(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			var entry = Find(key);
			if (entry is null) {
				// Started stays false until a user message is adopted off the hook stream.
				entry = new Entry { Key = key, Id = Guid.NewGuid().ToString(), Started = false };
				_items.Add(entry);
				PersistLocked();
				return new ClaudeLaunch(entry.Id, Resume: false);
			}

			return new ClaudeLaunch(entry.Id, entry.Started);
		}
	}

	/// <summary>
	/// Records that a <c>--resume</c> of <paramref name="workingDirectory"/> could not find the session, so the
	/// next <see cref="Resolve"/> re-creates it fresh under the same id rather than crash-looping on a doomed resume.
	/// </summary>
	public void MarkResumeFailed(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			if (Find(key) is { Started: true } entry) {
				entry.Started = false;
				PersistLocked();
			}
		}
	}

	/// <summary>
	/// Abandons <paramref name="workingDirectory"/>'s assigned id entirely (next <see cref="Resolve"/>
	/// cold-starts a new one). Used when even re-creating the id fails — it's poison (claude blocks reuse, yet
	/// its conversation is gone) — so unlike <see cref="MarkResumeFailed"/> there's nothing to preserve.
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
	/// off the hook stream on a real user message) and marks it started, realigning the store after claude
	/// rotated its id out from under Weavie (chiefly a <c>/clear</c>). No-op when the id already matches and is
	/// started, so the normal flow never thrashes the file.
	/// </summary>
	public void Adopt(string workingDirectory, string sessionId) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			var entry = Find(key);
			if (entry is null) {
				_items.Add(new Entry { Key = key, Id = sessionId, Started = true });
				PersistLocked();
				return;
			}

			if (!string.Equals(entry.Id, sessionId, StringComparison.Ordinal) || !entry.Started) {
				entry.Id = sessionId;
				entry.Started = true;
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
				.Select(e => new Entry { Key = e.Cwd, Id = e.Id, Started = e.Started })];
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
				Sessions = [.. _items.Select(e => new SessionEntry { Cwd = e.Key, Id = e.Id, Started = e.Started })],
			};
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(document, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[claude-sessions] could not persist: {ex.Message}");
		}
	}

	private sealed class Entry {
		public required string Key { get; init; }
		public required string Id { get; set; }
		public bool Started { get; set; }
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

		[JsonPropertyName("started")]
		public bool Started { get; set; }
	}
}
