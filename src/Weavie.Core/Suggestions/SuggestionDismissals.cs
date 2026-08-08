using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Suggestions;

/// <summary>
/// The per-workspace record of suggestions the user dismissed forever ("don't ask again"), persisted to
/// <c>~/.weavie/workspaces/&lt;id&gt;/suggestions.json</c>. Atomic writes; a malformed file is backed up to
/// <c>suggestions.json.bad</c> and reset rather than throwing. Snooze ("not now") is in-memory and lives in
/// <see cref="SuggestionService"/>, not here — only the durable "don't ask again" is persisted.
/// </summary>
public sealed class SuggestionDismissals {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly HashSet<string> _dismissed;

	/// <summary>Creates the store over <paramref name="path"/>, loading it now.</summary>
	public SuggestionDismissals(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
		lock (_gate) {
			_dismissed = LoadLocked();
		}
	}

	/// <summary>Diagnostic log line — read failures, malformed-file resets, persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>Whether <paramref name="id"/> was dismissed forever in this workspace.</summary>
	public bool IsDismissed(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (_gate) {
			return _dismissed.Contains(id);
		}
	}

	/// <summary>Records <paramref name="id"/> as dismissed forever and persists.</summary>
	public void Add(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (_gate) {
			if (_dismissed.Add(id)) {
				PersistLocked();
			}
		}
	}

	private HashSet<string> LoadLocked() =>
		JsonStoreFile.Load<HashSet<string>>(
			_fileSystem,
			FilePath,
			text => {
				var document = JsonSerializer.Deserialize<DismissalsDocument>(text);
				return document?.Dismissed is { } ids
					? new HashSet<string>(ids.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal)
					: new HashSet<string>(StringComparer.Ordinal);
			},
			static () => new HashSet<string>(StringComparer.Ordinal),
			Log);

	private void PersistLocked() {
		var document = new DismissalsDocument {
			Version = 1,
			Dismissed = [.. _dismissed],
		};
		JsonStoreFile.Persist(
			_fileSystem,
			FilePath,
			JsonSerializer.Serialize(document, JsonOptions),
			Log);
	}

	private sealed class DismissalsDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("dismissed")]
		public List<string> Dismissed { get; set; } = [];
	}
}
