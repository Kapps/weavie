namespace Weavie.Core.Spelling;

/// <summary>Checks text against Hunspell, software vocabulary, and Project/User dictionary overlays.</summary>
public sealed class SpellChecker {
	private readonly SpellCatalog _catalog;
	private readonly IReadOnlyList<CustomDictionary> _dictionaries;

	/// <summary>Creates a checker over one shared catalog and the supplied Project/User dictionary overlays.</summary>
	public SpellChecker(SpellCatalog catalog, IReadOnlyList<CustomDictionary> dictionaries) {
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(dictionaries);
		_catalog = catalog;
		_dictionaries = [.. dictionaries];
	}

	/// <summary>Returns all misspelled components in <paramref name="text"/> as zero-based UTF-16 ranges.</summary>
	public IReadOnlyList<SpellIssue> Check(
		string text,
		string locale,
		CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(text);
		ArgumentException.ThrowIfNullOrEmpty(locale);
		return [.. SpellTokenizer.MisspelledParts(text, word => IsCorrect(word, locale, cancellationToken))];
	}

	/// <summary>Returns up to five suggestions for a misspelled word.</summary>
	public IReadOnlyList<string> Suggest(string word, string locale, CancellationToken cancellationToken) {
		ArgumentException.ThrowIfNullOrEmpty(word);
		ArgumentException.ThrowIfNullOrEmpty(locale);
		return IsCorrect(word, locale, cancellationToken)
			? []
			: _catalog.Suggest(word, locale, cancellationToken);
	}

	private bool IsCorrect(string word, string locale, CancellationToken cancellationToken) {
		cancellationToken.ThrowIfCancellationRequested();
		string normalized = SpellWord.TryNormalize(word, out string normalizedWord) ? normalizedWord : word;
		foreach (var dictionary in _dictionaries) {
			if (dictionary.Contains(normalized)) {
				return true;
			}
		}

		return SpellVocabulary.Contains(normalized) || _catalog.Check(normalized, locale, cancellationToken);
	}
}
