using System.Collections.Frozen;
using WeCantSpell.Hunspell;

namespace Weavie.Core.Spelling;

/// <summary>An immutable language dictionary with optional English technical vocabulary and possessives.</summary>
public sealed class SpellVocabulary {
	private static readonly Lazy<TechnicalWords> Technical = new(() => {
		using var stream = typeof(SpellVocabulary).Assembly.GetManifestResourceStream("Weavie.Core.Spelling.Resources.technical.dic")!;
		using var reader = new StreamReader(stream);
		_ = reader.ReadLine();
		var words = new List<string>();
		while (reader.ReadLine() is { } word) words.Add(word);
		var suggestions = WordList.CreateFromWords(words);
		return new(words.ToFrozenSet(StringComparer.OrdinalIgnoreCase), suggestions);
	});
	internal static readonly Lazy<SpellVocabulary> English = new(() => new(Load("en"), english: true));
	private readonly WordList[] _dictionaries;
	private readonly FrozenSet<string> _technical;
	private readonly bool _english;

	/// <summary>Wraps a language dictionary, adding technical vocabulary and possessives for English selections.</summary>
	public SpellVocabulary(WordList dictionary, bool english) {
		_dictionaries = english ? [dictionary, Technical.Value.Suggestions] : [dictionary];
		_technical = english ? Technical.Value.Accepted : [];
		_english = english;
	}

	/// <summary>Accepts words recognized by any selected dictionary; in English, also any known word's possessive.</summary>
	public bool Check(string word, CancellationToken ct) =>
		Known(word, ct) || (_english && word.EndsWith("'s", StringComparison.Ordinal) && Known(word[..^2], ct));

	/// <summary>Checks a word without cancellation.</summary>
	public bool Check(string word) => Check(word, CancellationToken.None);

	/// <summary>Suggests corrections from the selected dictionaries, removing duplicates in source order.</summary>
	public IEnumerable<string> Suggest(string word, CancellationToken ct) =>
		_dictionaries.SelectMany(dictionary => dictionary.Suggest(word, ct)).Distinct(StringComparer.Ordinal);

	/// <summary>Suggests corrections without cancellation.</summary>
	public IEnumerable<string> Suggest(string word) => Suggest(word, CancellationToken.None);

	// Hunspell's English data marks possessives on only some nouns, so "delay's" is rejected despite "delay".
	private bool Known(string word, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		return _technical.Contains(word) || _dictionaries.Any(dictionary => dictionary.Check(word, ct));
	}

	private static WordList Load(string name) {
		var assembly = typeof(SpellVocabulary).Assembly;
		using var dic = assembly.GetManifestResourceStream($"Weavie.Core.Spelling.Resources.{name}.dic")!;
		using var aff = assembly.GetManifestResourceStream($"Weavie.Core.Spelling.Resources.{name}.aff")!;
		return WordList.CreateFromStreams(dic, aff);
	}

	private sealed record TechnicalWords(FrozenSet<string> Accepted, WordList Suggestions);
}
