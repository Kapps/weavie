using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Review;

/// <summary>
/// The session overview's app-global collapsed sections, persisted atomically to
/// <c>~/.weavie/review-overview-state.json</c>. Runtime UI state the host owns for the web, never settings.toml.
/// </summary>
public sealed partial class ReviewOverviewStateStore : JsonDocumentStore {
	private List<string> _collapsed = [];

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/review-overview-state.json</c>), loading it now.</summary>
	public ReviewOverviewStateStore(IFileSystem fileSystem, string? path)
		: base(fileSystem, path ?? WeaviePaths.ReviewOverviewStateFile) {
		Load();
	}

	/// <summary>Raised (off the UI thread) after the state changes, so each window re-pushes it to its page.</summary>
	public event Action? Changed;

	/// <summary>The collapsed section ids. Snapshot copy; safe to enumerate.</summary>
	public IReadOnlyList<string> Collapsed {
		get { lock (Gate) { return [.. _collapsed]; } }
	}

	/// <summary>Collapses or expands <paramref name="section"/>. No-op (no write, no event) when unchanged.</summary>
	public void SetCollapsed(string section, bool collapsed) {
		ArgumentException.ThrowIfNullOrWhiteSpace(section);
		lock (Gate) {
			if (_collapsed.Contains(section, StringComparer.Ordinal) == collapsed) {
				return;
			}

			_collapsed = collapsed ? [.. _collapsed, section] : [.. _collapsed.Where(s => s != section)];
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		var document = (text is null ? null : JsonSerializer.Deserialize(text, ReviewOverviewStateJson.Default.Document)) ?? new Document();
		_collapsed = [.. document.Collapsed.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal)];
	}

	/// <inheritdoc/>
	protected override string Render() =>
		JsonSerializer.Serialize(new Document { Collapsed = _collapsed }, ReviewOverviewStateJson.Default.Document);

	private sealed class Document {
		[JsonPropertyName("collapsed")]
		public List<string> Collapsed { get; set; } = [];
	}

	[JsonSourceGenerationOptions(WriteIndented = true)]
	[JsonSerializable(typeof(Document))]
	private sealed partial class ReviewOverviewStateJson : JsonSerializerContext;
}
