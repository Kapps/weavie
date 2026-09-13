using System.Text;
using System.Text.RegularExpressions;

namespace Weavie.Core.Spelling;

/// <summary>A prose or identifier span at a zero-based UTF-16 offset in its editor line.</summary>
public sealed record SpellSpan(int Line, int Offset, string Text, bool Identifier);

/// <summary>A misspelled word's exact editor location.</summary>
public sealed record Misspelling(int Line, int Offset, string Word);

/// <summary>Stateless spelling checks over editor-supplied prose and identifiers.</summary>
public static partial class SpellChecker {
	/// <summary>Checks spans without retaining document state; cancellation stops obsolete work.</summary>
	public static Misspelling[] Check(
		SpellVocabulary dictionary,
		IReadOnlyList<SpellSpan> spans,
		IReadOnlySet<string> userWords,
		IReadOnlySet<string> projectWords,
		CancellationToken ct) {
		var results = new List<Misspelling>();
		var checkedWords = new Dictionary<string, bool>(StringComparer.Ordinal);
		bool Accepted(string word) {
			ct.ThrowIfCancellationRequested();
			string normalized = Normalize(word);
			if (!checkedWords.TryGetValue(normalized, out bool correct)) {
				correct = userWords.Contains(normalized) || projectWords.Contains(normalized) || dictionary.Check(normalized, ct);
				checkedWords.Add(normalized, correct);
			}
			return correct;
		}
		foreach (var span in spans) {
			foreach (var (token, index) in TokensIn(span)) {
				if (IsWord(token) && Accepted(token)) continue;
				foreach (Match part in IdentifierWords().Matches(token)) {
					string word = part.Value;
					if (!IsWord(word) || Accepted(word)) continue;
					if (AcronymPlural().IsMatch(word) && Accepted(word[..^1])) continue;
					results.Add(new(span.Line, span.Offset + index + part.Index, word));
				}
			}
		}
		return [.. results];
	}

	private static IEnumerable<(string Word, int Index)> TokensIn(SpellSpan span) {
		if (span.Identifier) {
			yield return (span.Text, 0);
			yield break;
		}
		foreach (Match token in Tokens().Matches(span.Text)) {
			if (token.Groups["word"].Success) yield return (token.Value, token.Index);
		}
	}

	/// <summary>Canonical spelling for dictionary queries; editor offsets remain untouched.</summary>
	public static string Normalize(string word) => word.Replace('’', '\'').Normalize(NormalizationForm.FormC);

	/// <summary>Whether a value is one dictionary word, including an apostrophe.</summary>
	public static bool IsWord(string word) => Word().IsMatch(word);

	[GeneratedRegex(@"\A\p{L}[\p{L}\p{M}]*(?:['’]\p{L}[\p{L}\p{M}]*)*\z", RegexOptions.NonBacktracking)]
	private static partial Regex Word();

	[GeneratedRegex(@"(?:\p{Lu}{2,}s(?=\p{Lu}|[^\p{L}\p{M}]|$)|\p{Lu}+(?=\p{Lu}\p{Ll}|[^\p{L}\p{M}]|$)|\p{Lu}?[\p{Ll}\p{M}]+|\p{Lu}+|\p{L}[\p{L}\p{M}]*)(?:['’]\p{L}[\p{L}\p{M}]*)*")]
	private static partial Regex IdentifierWords();

	[GeneratedRegex(@"\A\p{Lu}{2,}s\z", RegexOptions.NonBacktracking)]
	private static partial Regex AcronymPlural();

	// Consume links, paths, and inline code as units so their components are not flagged as prose.
	[GeneratedRegex(@"https?://\S+|\S+[@/\\]\S*|`[^`]*`|(?<word>[\p{L}\p{M}\p{N}_]+(?:['’][\p{L}\p{M}\p{N}_]+)*)", RegexOptions.NonBacktracking)]
	private static partial Regex Tokens();
}
