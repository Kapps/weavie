namespace Weavie.Core.Search;

/// <summary>The recent find-in-files search terms (a bounded MRU list), cycled in the panel with Alt+Up/Down.</summary>
public static class SearchHistory {
	/// <summary>The most recent terms kept; older ones fall off the end.</summary>
	public const int Cap = 50;

	/// <summary>
	/// <paramref name="existing"/> with <paramref name="term"/> promoted to the front: trimmed, an empty term
	/// ignored (returns the list unchanged, deduped), any prior equal entry removed so it can't appear twice,
	/// and the result capped at <see cref="Cap"/>. Pure, so the MRU rules are testable without a store.
	/// </summary>
	public static IReadOnlyList<string> Add(IReadOnlyList<string> existing, string term) {
		ArgumentNullException.ThrowIfNull(existing);
		ArgumentNullException.ThrowIfNull(term);
		var kept = existing.Where(t => !string.IsNullOrWhiteSpace(t));
		string trimmed = term.Trim();
		if (trimmed.Length == 0) {
			return [.. Dedupe(kept).Take(Cap)];
		}

		return [.. Dedupe(kept.Where(t => !string.Equals(t, trimmed, StringComparison.Ordinal)).Prepend(trimmed)).Take(Cap)];
	}

	private static IEnumerable<string> Dedupe(IEnumerable<string> terms) {
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (string term in terms) {
			if (seen.Add(term)) {
				yield return term;
			}
		}
	}
}
