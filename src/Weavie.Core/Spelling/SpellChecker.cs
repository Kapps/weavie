using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace Weavie.Core.Spelling;

/// <summary>A prose span at a zero-based UTF-16 offset in its editor line.</summary>
public sealed record SpellSpan(int Line, int Offset, string Text);

/// <summary>A misspelled word's exact editor location.</summary>
public sealed record Misspelling(int Line, int Offset, string Word);

/// <summary>Stateless English spelling checks over editor-supplied prose.</summary>
public static partial class SpellChecker {
	private static readonly Lazy<WordList> English = new(() => {
		var assembly = typeof(SpellChecker).Assembly;
		using var dic = assembly.GetManifestResourceStream("Weavie.Core.Spelling.Resources.en_US.dic")!;
		using var aff = assembly.GetManifestResourceStream("Weavie.Core.Spelling.Resources.en_US.aff")!;
		return WordList.CreateFromStreams(dic, aff);
	});

	/// <summary>Checks spans without retaining document state; cancellation stops obsolete work.</summary>
	public static Misspelling[] Check(
		IReadOnlyList<SpellSpan> spans,
		IReadOnlySet<string> userWords,
		IReadOnlySet<string> projectWords,
		CancellationToken ct) {
		var dictionary = English.Value;
		var results = new List<Misspelling>();
		var checkedWords = new Dictionary<string, bool>(StringComparer.Ordinal);
		foreach (var span in spans) {
			foreach (Match match in Tokens().Matches(span.Text)) {
				ct.ThrowIfCancellationRequested();
				string word = match.Value;
				if (!IsWord(word) || userWords.Contains(word) || projectWords.Contains(word)) {
					continue;
				}
				if (!checkedWords.TryGetValue(word, out bool correct)) {
					correct = dictionary.Check(word.Replace('’', '\''));
					checkedWords.Add(word, correct);
				}
				if (!correct) {
					results.Add(new(span.Line, span.Offset + match.Index, word));
				}
			}
		}
		return [.. results];
	}

	/// <summary>Whether a value is one dictionary word, including an apostrophe.</summary>
	public static bool IsWord(string word) => Word().IsMatch(word);

	[GeneratedRegex(@"\A\p{L}+(?:['’]\p{L}+)*\z", RegexOptions.NonBacktracking)]
	private static partial Regex Word();

	// Consume links, paths, and inline code as units so their components are not flagged as prose.
	[GeneratedRegex(@"https?://\S+|\S+[@/\\]\S*|`[^`]*`|[\p{L}\p{N}_]+(?:['’][\p{L}\p{N}_]+)*", RegexOptions.NonBacktracking)]
	private static partial Regex Tokens();
}
