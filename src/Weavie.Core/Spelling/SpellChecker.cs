using System.Text;
using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace Weavie.Core.Spelling;

/// <summary>A prose or identifier span at a zero-based UTF-16 offset in its editor line.</summary>
public sealed record SpellSpan(int Line, int Offset, string Text, bool Identifier);

/// <summary>A misspelled word's exact editor location.</summary>
public sealed record Misspelling(int Line, int Offset, string Word);

/// <summary>Stateless spelling checks over editor-supplied prose and identifiers.</summary>
public static partial class SpellChecker {
	internal static readonly Lazy<WordList> English = new(() => {
		var assembly = typeof(SpellChecker).Assembly;
		using var dic = assembly.GetManifestResourceStream("Weavie.Core.Spelling.Resources.en_US.dic")!;
		using var aff = assembly.GetManifestResourceStream("Weavie.Core.Spelling.Resources.en_US.aff")!;
		return WordList.CreateFromStreams(dic, aff);
	});

	/// <summary>Checks spans without retaining document state; cancellation stops obsolete work.</summary>
	public static Misspelling[] Check(
		WordList dictionary,
		IReadOnlyList<SpellSpan> spans,
		IReadOnlySet<string> userWords,
		IReadOnlySet<string> projectWords,
		CancellationToken ct) {
		var results = new List<Misspelling>();
		var checkedWords = new Dictionary<string, bool>(StringComparer.Ordinal);
		foreach (var span in spans) {
			foreach (Match match in (span.Identifier ? IdentifierWords() : Tokens()).Matches(span.Text)) {
				ct.ThrowIfCancellationRequested();
				string word = match.Value;
				if (!IsWord(word)) continue;
				string normalized = Normalize(word);
				if (userWords.Contains(normalized) || projectWords.Contains(normalized)) {
					continue;
				}
				if (!checkedWords.TryGetValue(normalized, out bool correct)) {
					correct = dictionary.Check(normalized, ct);
					checkedWords.Add(normalized, correct);
				}
				if (!correct) {
					results.Add(new(span.Line, span.Offset + match.Index, word));
				}
			}
		}
		return [.. results];
	}

	/// <summary>Canonical spelling for dictionary queries; editor offsets remain untouched.</summary>
	public static string Normalize(string word) => word.Replace('’', '\'').Normalize(NormalizationForm.FormC);

	/// <summary>Whether a value is one dictionary word, including an apostrophe.</summary>
	public static bool IsWord(string word) => Word().IsMatch(word);

	[GeneratedRegex(@"\A\p{L}[\p{L}\p{M}]*(?:['’]\p{L}[\p{L}\p{M}]*)*\z", RegexOptions.NonBacktracking)]
	private static partial Regex Word();

	[GeneratedRegex(@"\p{Lu}+(?=\p{Lu}\p{Ll}|[^\p{L}\p{M}]|$)|\p{Lu}?[\p{Ll}\p{M}]+|\p{Lu}+|\p{L}[\p{L}\p{M}]*")]
	private static partial Regex IdentifierWords();

	// Consume links, paths, and inline code as units so their components are not flagged as prose.
	[GeneratedRegex(@"https?://\S+|\S+[@/\\]\S*|`[^`]*`|[\p{L}\p{M}\p{N}_]+(?:['’][\p{L}\p{M}\p{N}_]+)*", RegexOptions.NonBacktracking)]
	private static partial Regex Tokens();
}
