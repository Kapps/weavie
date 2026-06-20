using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// How the next <c>claude</c> launch in a given working directory should be wired: the stable session id
/// Weavie assigned that directory, and whether to <c>--resume</c> it (the session already exists) or start
/// it fresh with <c>--session-id</c> (the first launch). <see cref="SessionId"/> is never empty.
/// </summary>
/// <param name="SessionId">The UUID Weavie owns for this working directory's Claude conversation.</param>
/// <param name="Resume">True to reattach (<c>--resume</c>); false to create it (<c>--session-id</c>).</param>
public readonly record struct ClaudeLaunch(string SessionId, bool Resume);

/// <summary>
/// Remembers the Claude Code session id Weavie assigned to each working directory, persisted to
/// <c>~/.weavie/claude-sessions.json</c>, so reopening a Weavie session resumes its previous Claude
/// conversation instead of cold-starting a new one. Weavie <em>assigns</em> the id (a fresh UUID passed as
/// <c>--session-id</c> on the first launch) rather than scraping Claude's storage, so the id is known up
/// front and resume is deterministic — the same directory always reattaches to the same conversation via
/// <c>--resume</c>. Keyed by the launch directory (which is per-session: each worktree session has its own),
/// so multiple parallel sessions each track their own Claude. Mirrors <see cref="SessionStore"/>'s
/// conventions: atomic writes; a malformed file is backed up to <c>claude-sessions.json.bad</c> and reset
/// rather than throwing.
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
	/// session id the first time the directory is seen (returning <see cref="ClaudeLaunch.Resume"/> false so
	/// the caller starts it with <c>--session-id</c>), and on every later call returns the same id with
	/// <c>Resume</c> true so the caller reattaches with <c>--resume</c>. Persists only when something changed.
	/// </summary>
	public ClaudeLaunch Resolve(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			var entry = Find(key);
			if (entry is null) {
				// First launch in this directory: assign a fresh id and create the session with --session-id.
				entry = new Entry { Key = key, Id = Guid.NewGuid().ToString(), Started = true };
				_items.Add(entry);
				PersistLocked();
				return new ClaudeLaunch(entry.Id, Resume: false);
			}

			// Seen before. If we already launched it, reattach; otherwise (a prior resume failed and reset it)
			// re-create it under the same id, then mark it started so the next launch reattaches again.
			bool resume = entry.Started;
			if (!entry.Started) {
				entry.Started = true;
				PersistLocked();
			}

			return new ClaudeLaunch(entry.Id, resume);
		}
	}

	/// <summary>
	/// Records that a <c>--resume</c> of <paramref name="workingDirectory"/>'s session could not find it (e.g.
	/// Claude pruned the transcript after its retention period). Forgets that the id has started, so the next
	/// <see cref="Resolve"/> re-creates it fresh under the <em>same</em> id (<c>--session-id</c>) — keeping the
	/// directory's session identity stable — rather than crash-looping on a resume that can never succeed.
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

	private Entry? Find(string key) => _items.FirstOrDefault(e => PathEquals(e.Key, key));

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
			BackupBadFileLocked(text);
			return [];
		}
	}

	private void BackupBadFileLocked(string text) {
		try {
			_fileSystem.WriteAllText(FilePath + ".bad", text);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[claude-sessions] could not back up malformed file: {ex.Message}");
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
		public required string Id { get; init; }
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
