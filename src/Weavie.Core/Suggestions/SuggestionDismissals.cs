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
public sealed class SuggestionDismissals : JsonDocumentStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private HashSet<string> _dismissed = new(StringComparer.Ordinal);

	/// <summary>Creates the store over <paramref name="path"/>, loading it now.</summary>
	/// <param name="fileSystem">The filesystem the dismissals persist through.</param>
	/// <param name="path">The backing file.</param>
	public SuggestionDismissals(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <summary>Whether <paramref name="id"/> was dismissed forever in this workspace.</summary>
	public bool IsDismissed(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (Gate) {
			return _dismissed.Contains(id);
		}
	}

	/// <summary>Records <paramref name="id"/> as dismissed forever and persists.</summary>
	public void Add(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (Gate) {
			if (_dismissed.Add(id)) {
				PersistLocked();
			}
		}
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		var document = text is null ? null : JsonSerializer.Deserialize<DismissalsDocument>(text);
		_dismissed = new HashSet<string>(
			document?.Dismissed?.Where(id => !string.IsNullOrWhiteSpace(id)) ?? [],
			StringComparer.Ordinal);
	}

	/// <inheritdoc/>
	protected override string Render() =>
		JsonSerializer.Serialize(new DismissalsDocument { Version = 1, Dismissed = [.. _dismissed] }, JsonOptions);

	private sealed class DismissalsDocument {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("dismissed")]
		public List<string> Dismissed { get; set; } = [];
	}
}
