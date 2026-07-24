using WeCantSpell.Hunspell;

namespace Weavie.Core.Spelling;

/// <summary>Thread-safe, lazily loaded embedded Hunspell dictionaries.</summary>
public sealed class SpellCatalog {
	private const string ResourcePrefix = "Weavie.Core.Spelling.Resources.";
	private static readonly QueryOptions SuggestionOptions = new() {
		MaxSuggestions = 5,
		TimeLimitSuggestStep = TimeSpan.FromTicks(-1),
		TimeLimitCompoundSuggest = TimeSpan.FromTicks(-1),
		TimeLimitCompoundCheck = TimeSpan.FromTicks(-1),
		TimeLimitSuggestGlobal = TimeSpan.FromTicks(-1),
	};
	private static readonly Lazy<SpellCatalog> Embedded = new(CreateEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

	private readonly IReadOnlyDictionary<string, Lazy<WordList>> _dictionaries;

	private SpellCatalog(IReadOnlyDictionary<string, Lazy<WordList>> dictionaries) {
		_dictionaries = dictionaries;
	}

	/// <summary>Creates a catalog whose embedded dictionaries load only when their locale is first used.</summary>
	public static SpellCatalog LoadEmbedded() => Embedded.Value;

	private static SpellCatalog CreateEmbedded() {
		var dictionaries = new Dictionary<string, Lazy<WordList>>(StringComparer.Ordinal);
		foreach (string locale in SpellLocales.Supported) {
			dictionaries.Add(locale, new Lazy<WordList>(
				() => LoadLocale(locale), LazyThreadSafetyMode.ExecutionAndPublication));
		}

		return new SpellCatalog(dictionaries);
	}

	/// <summary>Returns whether <paramref name="word"/> is valid in <paramref name="locale"/>.</summary>
	public bool Check(string word, string locale, CancellationToken cancellationToken) {
		ArgumentException.ThrowIfNullOrEmpty(word);
		return DictionaryFor(locale).Check(word, cancellationToken);
	}

	/// <summary>Returns up to five Hunspell suggestions for <paramref name="word"/> in <paramref name="locale"/>.</summary>
	public IReadOnlyList<string> Suggest(string word, string locale, CancellationToken cancellationToken) {
		ArgumentException.ThrowIfNullOrEmpty(word);
		cancellationToken.ThrowIfCancellationRequested();
		IReadOnlyList<string> suggestions = [.. DictionaryFor(locale).Suggest(word, SuggestionOptions, cancellationToken)];
		cancellationToken.ThrowIfCancellationRequested();
		return suggestions;
	}

	private WordList DictionaryFor(string locale) =>
		_dictionaries.TryGetValue(locale, out var dictionary)
			? dictionary.Value
			: throw new ArgumentException($"Unsupported spell-check locale '{locale}'.", nameof(locale));

	private static WordList LoadLocale(string locale) {
		string stem = SpellLocales.ResourceStem(locale);
		var assembly = typeof(SpellCatalog).Assembly;
		using var dictionary = RequiredResource(assembly, stem, "dic");
		using var affix = RequiredResource(assembly, stem, "aff");
		return WordList.CreateFromStreams(dictionary, affix);
	}

	private static Stream RequiredResource(System.Reflection.Assembly assembly, string stem, string extension) =>
		assembly.GetManifestResourceStream($"{ResourcePrefix}{stem}.{extension}")
			?? throw new InvalidOperationException($"Missing embedded spell-check resource '{stem}.{extension}'.");
}
