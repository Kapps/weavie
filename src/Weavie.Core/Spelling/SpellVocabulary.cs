using System.Collections.Frozen;
using WeCantSpell.Hunspell;

namespace Weavie.Core.Spelling;

/// <summary>An immutable selection of regional dictionaries and optional technical vocabulary.</summary>
public sealed class SpellVocabulary {
	private static readonly Lazy<WordList> EnglishUs = new(() => Load("en_US-large"));
	private static readonly Lazy<WordList> EnglishCa = new(() => Load("en_CA-large"));
	private static readonly Lazy<WordList> EnglishGb = new(() => Load("en_GB-large"));
	private static readonly Lazy<TechnicalWords> Technical = new(() => {
		using var stream = typeof(SpellVocabulary).Assembly.GetManifestResourceStream("Weavie.Core.Spelling.Resources.technical.dic")!;
		using var reader = new StreamReader(stream);
		_ = reader.ReadLine();
		var words = new List<string>();
		while (reader.ReadLine() is { } word) words.Add(word);
		var suggestions = WordList.CreateFromWords(words);
		return new(words.ToFrozenSet(StringComparer.OrdinalIgnoreCase), suggestions);
	});
	private static readonly FrozenDictionary<string, Lazy<SpellVocabulary>> Bundled =
		new Dictionary<string, Lazy<SpellVocabulary>> {
			["en"] = new(() => new([EnglishUs.Value, EnglishCa.Value, EnglishGb.Value], Technical.Value)),
			["en-US"] = new(() => new([EnglishUs.Value], Technical.Value)),
			["en-CA"] = new(() => new([EnglishCa.Value], Technical.Value)),
			["en-GB"] = new(() => new([EnglishGb.Value], Technical.Value)),
		}.ToFrozenDictionary();
	private readonly WordList[] _dictionaries;
	private readonly FrozenSet<string> _technical;

	private SpellVocabulary(WordList[] regional, TechnicalWords technical) {
		_dictionaries = [.. regional, technical.Suggestions];
		_technical = technical.Accepted;
	}

	/// <summary>Wraps an installed non-English dictionary without adding English vocabulary.</summary>
	public SpellVocabulary(WordList dictionary) {
		_dictionaries = [dictionary];
		_technical = [];
	}

	/// <summary>Locale codes available without downloading dictionaries.</summary>
	public static IEnumerable<string> BundledLocales => Bundled.Keys.Order(StringComparer.Ordinal);

	/// <summary>Whether a locale is bundled; this does not load its dictionaries.</summary>
	public static bool IsBundled(string locale) => Bundled.ContainsKey(locale);

	/// <summary>Loads a bundled vocabulary on its first background request.</summary>
	public static SpellVocabulary ForLocale(string locale) => Bundled[locale].Value;

	/// <summary>Accepts words recognized by any selected dictionary.</summary>
	public bool Check(string word, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		return _technical.Contains(word) || _dictionaries.Any(dictionary => dictionary.Check(word, ct));
	}

	/// <summary>Checks a word without cancellation.</summary>
	public bool Check(string word) => Check(word, CancellationToken.None);

	/// <summary>Suggests corrections from the selected dictionaries, removing duplicates in source order.</summary>
	public IEnumerable<string> Suggest(string word, CancellationToken ct) =>
		_dictionaries.SelectMany(dictionary => dictionary.Suggest(word, ct)).Distinct(StringComparer.Ordinal);

	/// <summary>Suggests corrections without cancellation.</summary>
	public IEnumerable<string> Suggest(string word) => Suggest(word, CancellationToken.None);

	private static WordList Load(string name) {
		var assembly = typeof(SpellVocabulary).Assembly;
		using var dic = assembly.GetManifestResourceStream($"Weavie.Core.Spelling.Resources.{name}.dic")!;
		using var aff = assembly.GetManifestResourceStream($"Weavie.Core.Spelling.Resources.{name}.aff")!;
		return WordList.CreateFromStreams(dic, aff);
	}

	private sealed record TechnicalWords(FrozenSet<string> Accepted, WordList Suggestions);
}
