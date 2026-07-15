using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>A stored Codex thread for a worktree, including its conversation-local collaboration mode.</summary>
public readonly record struct CodexThreadLaunch(string? ThreadId, bool Resume, string Mode);

/// <summary>Persists one Codex app-server thread id per working directory.</summary>
public sealed class CodexThreadStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly List<Entry> _items;

	/// <summary>Creates the store over <paramref name="path"/>, loading it now.</summary>
	public CodexThreadStore(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
		lock (_gate) {
			_items = LoadLocked();
		}
	}

	/// <summary>Diagnostic log line for read, parse, and persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>Returns the existing Codex thread for <paramref name="workingDirectory"/>, or create-new intent.</summary>
	public CodexThreadLaunch Resolve(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			var entry = Find(key);
			return entry is null
				? new CodexThreadLaunch(null, Resume: false, Mode: string.Empty)
				: new CodexThreadLaunch(entry.Id, Resume: true, entry.Mode);
		}
	}

	/// <summary>Records the Codex thread id used for <paramref name="workingDirectory"/>.</summary>
	public void Adopt(string workingDirectory, string threadId) => Adopt(workingDirectory, threadId, string.Empty);

	/// <summary>Records the Codex thread id and collaboration mode used for <paramref name="workingDirectory"/>.</summary>
	public void Adopt(string workingDirectory, string threadId, string mode) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentNullException.ThrowIfNull(mode);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			var entry = Find(key);
			if (entry is null) {
				_items.Add(new Entry { Key = key, Id = threadId, Mode = mode });
				PersistLocked();
				return;
			}

			if (!string.Equals(entry.Id, threadId, StringComparison.Ordinal)
				|| !string.Equals(entry.Mode, mode, StringComparison.Ordinal)) {
				entry.Id = threadId;
				entry.Mode = mode;
				PersistLocked();
			}
		}
	}

	/// <summary>Updates the collaboration mode of an already-persisted Codex conversation.</summary>
	public void SetMode(string workingDirectory, string mode) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentException.ThrowIfNullOrEmpty(mode);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			var entry = Find(key);
			if (entry is not null && !string.Equals(entry.Mode, mode, StringComparison.Ordinal)) {
				entry.Mode = mode;
				PersistLocked();
			}
		}
	}

	/// <summary>Forgets the stored Codex thread for <paramref name="workingDirectory"/>.</summary>
	public void Clear(string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string key = Normalize(workingDirectory);
		lock (_gate) {
			if (_items.RemoveAll(e => PathEquals(e.Key, key)) > 0) {
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
			Log?.Invoke($"[codex-threads] could not read {FilePath}: {ex.Message}; starting empty");
			return [];
		}

		try {
			var document = JsonSerializer.Deserialize<Document>(text);
			if (document?.Threads is not { } entries) {
				return [];
			}

			return [.. entries
				.Where(e => !string.IsNullOrWhiteSpace(e.Cwd) && !string.IsNullOrWhiteSpace(e.Id))
				.Select(e => new Entry { Key = e.Cwd, Id = e.Id, Mode = e.Mode })];
		} catch (JsonException ex) {
			Log?.Invoke($"[codex-threads] {FilePath} is malformed ({ex.Message}); backing up to codex-threads.json.bad and resetting");
			JsonStoreFile.BackupBad(_fileSystem, FilePath, text, "codex-threads", Log);
			return [];
		}
	}

	private void PersistLocked() {
		try {
			var document = new Document {
				Version = 1,
				Threads = [.. _items.Select(e => new ThreadEntry { Cwd = e.Key, Id = e.Id, Mode = e.Mode })],
			};
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(document, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[codex-threads] could not persist: {ex.Message}");
		}
	}

	private sealed class Entry {
		public required string Key { get; init; }
		public required string Id { get; set; }
		public required string Mode { get; set; }
	}

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("threads")]
		public List<ThreadEntry> Threads { get; set; } = [];
	}

	private sealed class ThreadEntry {
		[JsonPropertyName("cwd")]
		public string Cwd { get; set; } = string.Empty;

		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;

		[JsonPropertyName("mode")]
		public string Mode { get; set; } = string.Empty;
	}
}
