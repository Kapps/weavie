using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// How the next <c>claude</c> launch in a given working directory should be wired: the stable session id
/// Weavie assigned that directory, and whether to <c>--resume</c> it or create it fresh with
/// <c>--session-id</c>. <see cref="SessionId"/> is never empty.
/// </summary>
/// <param name="SessionId">The UUID Weavie owns for this working directory's Claude conversation.</param>
/// <param name="Resume">True to reattach (<c>--resume</c>); false to create it (<c>--session-id</c>).</param>
public readonly record struct ClaudeLaunch(string SessionId, bool Resume);

/// <summary>
/// Remembers the Claude Code session id Weavie assigned to each working directory, persisted to
/// <c>~/.weavie/claude-sessions.json</c>, so reopening a Weavie session resumes its previous Claude
/// conversation instead of cold-starting. Weavie assigns the id (a fresh UUID passed as <c>--session-id</c>
/// on the first launch) rather than scraping Claude's storage, so resume is deterministic. Keyed by launch
/// directory (per-session), so parallel sessions each track their own Claude. Atomic writes; a malformed
/// file is backed up to <c>claude-sessions.json.bad</c> and reset rather than throwing.
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
	/// session id the first time the directory is seen (with <see cref="ClaudeLaunch.Resume"/> false, so the
	/// caller uses <c>--session-id</c>). Thereafter it reattaches (<c>Resume</c> true) only once a launch was
	/// confirmed via <see cref="MarkStarted"/>; until then — an unconfirmed create, or a
	/// <see cref="MarkResumeFailed">failed</see> resume — it re-creates under the same id. The id is never
	/// marked started here, so a launch that dies before the session exists is not mistaken for resumable.
	/// Persists only when it mints.
	/// </summary>
	public ClaudeLaunch Resolve(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			var entry = Find(key);
			if (entry is null) {
				// First sighting: assign a stable id and create with --session-id. Started stays false until
				// MarkStarted confirms claude came up.
				entry = new Entry { Key = key, Id = Guid.NewGuid().ToString(), Started = false };
				_items.Add(entry);
				PersistLocked();
				return new ClaudeLaunch(entry.Id, Resume: false);
			}

			// Reattach only if a prior launch was confirmed started; otherwise re-create under the same id.
			return new ClaudeLaunch(entry.Id, entry.Started);
		}
	}

	/// <summary>
	/// Confirms that <paramref name="workingDirectory"/>'s claude session is up — its id now exists, so the next
	/// launch reattaches with <c>--resume</c>. The inverse of <see cref="MarkResumeFailed"/>. Persists only on a
	/// change; a no-op if the directory was never <see cref="Resolve"/>d or is already marked.
	/// </summary>
	public void MarkStarted(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			if (Find(key) is { Started: false } entry) {
				entry.Started = true;
				PersistLocked();
			}
		}
	}

	/// <summary>
	/// Records that a <c>--resume</c> of <paramref name="workingDirectory"/>'s session could not find it (e.g.
	/// Claude pruned the transcript). Forgets that the id started, so the next <see cref="Resolve"/> re-creates
	/// it fresh under the same id — keeping identity stable — rather than crash-looping on a doomed resume.
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
	/// Abandons <paramref name="workingDirectory"/>'s assigned session id entirely, so the next
	/// <see cref="Resolve"/> mints a brand-new one and cold-starts. Used when even re-creating the id with
	/// <c>--session-id</c> fails — the id is poison (claude blocks reusing it, yet its conversation is gone) — so
	/// unlike <see cref="MarkResumeFailed"/> there is nothing to preserve. No-op if the directory was never
	/// <see cref="Resolve"/>d. Persists only on a change.
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
	/// Drops <paramref name="workingDirectory"/>'s tracked id because the user ran <c>/clear</c>: claude has left
	/// that conversation, so resuming the old id would reattach to the stale transcript the clear meant to
	/// escape. A relaunch right after a clear cold-starts fresh; the next real user message re-establishes
	/// tracking via <see cref="Adopt"/>. No-op if the directory was never <see cref="Resolve"/>d. Persists only
	/// on a change.
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
	/// Records the session id claude reports it is actually in for <paramref name="workingDirectory"/> (observed
	/// off the hook stream on a real user message), marking it started so the next launch <c>--resume</c>s that
	/// conversation. Realigns the store after claude rotated its id out from under Weavie (chiefly a <c>/clear</c>,
	/// but any drift). Creates the entry if cleared/forgotten; otherwise repoints the existing id. A no-op when
	/// the id already matches and is started, so the normal flow never thrashes the file. Persists only on a change.
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
