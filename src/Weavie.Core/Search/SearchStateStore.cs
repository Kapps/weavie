using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;

namespace Weavie.Core.Search;

/// <summary>The persisted find-in-files state: the last-used match options and the recent-terms history.</summary>
public sealed record SearchState {
	/// <summary>The match options + include/exclude globs (reuses the grep-execution shape — same fields).</summary>
	public required GrepOptions Options { get; init; }

	/// <summary>Recent search terms, most-recent first.</summary>
	public required IReadOnlyList<string> RecentTerms { get; init; }
}

/// <summary>
/// The find-in-files panel's app-global UI state — the match options, include/exclude globs, and recent
/// search terms — persisted atomically to <c>~/.weavie/search-state.json</c>. Its own file, never
/// settings.toml: runtime UI state the host owns on the web's behalf, off the Claude-facing settings surface
/// (mirrors <see cref="Sessions.RailStateStore"/>). The current search term is deliberately NOT persisted —
/// only the history is. A malformed file is backed up to <c>search-state.json.bad</c> and reset.
/// </summary>
public sealed class SearchStateStore : JsonDocumentStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private Document _doc = new();

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/search-state.json</c>), loading it now.</summary>
	/// <param name="fileSystem">The filesystem the state persists through.</param>
	/// <param name="path">The backing file, or <c>null</c> for the default.</param>
	public SearchStateStore(IFileSystem fileSystem, string? path)
		: base(fileSystem, path ?? WeaviePaths.SearchStateFile) {
		Load();
	}

	/// <summary>Raised (off the UI thread) after the state changes, so each window re-pushes it to its page.</summary>
	public event Action? Changed;

	/// <summary>The persisted match options + globs + recent terms. Snapshot copy; safe to enumerate.</summary>
	public SearchState Current {
		get { lock (Gate) { return _doc.ToState(); } }
	}

	/// <summary>Replaces the match options and include/exclude globs (never the recent terms). No-op when unchanged.</summary>
	public void SetOptions(GrepOptions options) {
		ArgumentNullException.ThrowIfNull(options);
		lock (Gate) {
			if (_doc.ToState().Options == options) {
				return;
			}

			_doc = _doc.WithOptions(options);
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>Records <paramref name="term"/> as the most recent search (MRU, deduped, bounded). No-op when it doesn't change the list.</summary>
	public void AddRecentTerm(string term) {
		ArgumentNullException.ThrowIfNull(term);
		lock (Gate) {
			var next = SearchHistory.Add(_doc.RecentTerms, term);
			if (next.SequenceEqual(_doc.RecentTerms, StringComparer.Ordinal)) {
				return;
			}

			_doc = _doc with { RecentTerms = [.. next] };
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) =>
		_doc = (text is null ? null : JsonSerializer.Deserialize<Document>(text))?.Sanitized() ?? new Document();

	/// <inheritdoc/>
	protected override string Render() => JsonSerializer.Serialize(_doc with { Version = 1 }, JsonOptions);

	// The on-disk shape. Options are flattened (not a nested GrepOptions) so the JSON stays a flat, hand-editable
	// document; excludeGitignored defaults true so a partial file keeps the sensible default.
	private sealed record Document {
		[JsonPropertyName("version")]
		public int Version { get; init; }

		[JsonPropertyName("caseSensitive")]
		public bool CaseSensitive { get; init; }

		[JsonPropertyName("wholeWord")]
		public bool WholeWord { get; init; }

		[JsonPropertyName("regex")]
		public bool Regex { get; init; }

		[JsonPropertyName("excludeGitignored")]
		public bool ExcludeGitignored { get; init; } = true;

		[JsonPropertyName("include")]
		public string Include { get; init; } = "";

		[JsonPropertyName("exclude")]
		public string Exclude { get; init; } = "";

		[JsonPropertyName("recentTerms")]
		public IReadOnlyList<string> RecentTerms { get; init; } = [];

		// Coalesce nulls a hand-edited file can introduce (JSON null on a reference field), so a bad edit resets
		// to sane values rather than throwing out of the constructor past the malformed-file guard.
		public Document Sanitized() => this with {
			Include = Include ?? "",
			Exclude = Exclude ?? "",
			RecentTerms = [.. SearchHistory.Add(RecentTerms ?? [], "")],
		};

		public Document WithOptions(GrepOptions o) => this with {
			CaseSensitive = o.CaseSensitive,
			WholeWord = o.WholeWord,
			Regex = o.Regex,
			ExcludeGitignored = o.ExcludeGitignored,
			Include = o.Include,
			Exclude = o.Exclude,
		};

		public SearchState ToState() => new() {
			Options = new GrepOptions {
				CaseSensitive = CaseSensitive,
				WholeWord = WholeWord,
				Regex = Regex,
				ExcludeGitignored = ExcludeGitignored,
				Include = Include,
				Exclude = Exclude,
			},
			RecentTerms = [.. RecentTerms],
		};
	}
}
