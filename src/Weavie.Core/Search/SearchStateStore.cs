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
public sealed class SearchStateStore {
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private Document _doc;

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/search-state.json</c>), loading it now.</summary>
	public SearchStateStore(IFileSystem fileSystem, string? path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
		FilePath = path ?? WeaviePaths.SearchStateFile;
		lock (_gate) {
			_doc = LoadLocked();
		}
	}

	/// <summary>Raised (off the UI thread) after the state changes, so each window re-pushes it to its page.</summary>
	public event Action? Changed;

	/// <summary>Diagnostic log line — read failures, malformed-file resets, persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The search-state file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>The persisted match options + globs + recent terms. Snapshot copy; safe to enumerate.</summary>
	public SearchState Current {
		get { lock (_gate) { return _doc.ToState(); } }
	}

	/// <summary>Replaces the match options and include/exclude globs (never the recent terms). No-op when unchanged.</summary>
	public void SetOptions(GrepOptions options) {
		ArgumentNullException.ThrowIfNull(options);
		lock (_gate) {
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
		lock (_gate) {
			var next = SearchHistory.Add(_doc.RecentTerms, term);
			if (next.SequenceEqual(_doc.RecentTerms, StringComparer.Ordinal)) {
				return;
			}

			_doc = _doc with { RecentTerms = [.. next] };
			PersistLocked();
		}

		Changed?.Invoke();
	}

	private Document LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return new Document();
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[search-state] could not read {FilePath}: {ex.Message}; starting empty");
			return new Document();
		}

		try {
			return (JsonSerializer.Deserialize<Document>(text) ?? new Document()).Sanitized();
		} catch (JsonException ex) {
			Log?.Invoke($"[search-state] {FilePath} is malformed ({ex.Message}); backing up to search-state.json.bad and resetting");
			JsonStoreFile.BackupBad(_fileSystem, FilePath, text, "search-state", Log);
			return new Document();
		}
	}

	private void PersistLocked() {
		try {
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(_doc with { Version = 1 }, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[search-state] could not persist: {ex.Message}");
		}
	}

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
