using System.Buffers;
using System.Text;

namespace Weavie.Core.Spelling;

internal static class SpellTokenizer {
	private const int MinWordLength = 3;

	internal static IEnumerable<SpellIssue> MisspelledParts(string text, Func<string, bool> isCorrect) {
		int index = 0;
		while (index < text.Length) {
			if (!TryCandidateRune(text, index, out var rune, out int width) || SpellWord.IsCombiningMark(rune)) {
				index += RuneWidth(text, index);
				continue;
			}

			int start = index;
			bool hasDigit = Rune.IsDigit(rune);
			index += width;
			while (index < text.Length && TryCandidateRune(text, index, out rune, out width)) {
				hasDigit |= Rune.IsDigit(rune);
				index += width;
			}

			var candidate = text.AsSpan(start, index - start);
			if (hasDigit || IsIgnored(candidate)) {
				continue;
			}

			foreach (var part in Parts(text, start, index)) {
				if (HasMinimumLetters(part.Word) && !IsAllUpper(part.Word) && !isCorrect(part.Word)) {
					yield return new SpellIssue(part.Start, part.Length, part.Word);
				}
			}
		}
	}

	private static IEnumerable<Part> Parts(string text, int start, int end) {
		int index = start;
		while (index < end) {
			if (!TryWordRune(text, index, out var rune, out int width) || SpellWord.IsCombiningMark(rune)) {
				index += RuneWidth(text, index);
				continue;
			}

			int wordStart = index;
			index += width;
			while (index < end && TryWordRune(text, index, out _, out width)) {
				index += width;
			}

			foreach (var part in SplitIdentifier(text, wordStart, index)) {
				yield return part;
			}
		}
	}

	private static IEnumerable<Part> SplitIdentifier(string text, int start, int end) {
		int partStart = start;
		int index = start;
		Rune? previous = null;
		while (index < end) {
			Rune.DecodeFromUtf16(text.AsSpan(index), out var current, out int width);
			int next = index + width;
			bool upperAfterLower = previous is { } prior && Rune.IsLower(prior) && Rune.IsUpper(current);
			bool upperBeforeLower = previous is { } upper && Rune.IsUpper(upper) && Rune.IsUpper(current)
				&& next < end && Rune.DecodeFromUtf16(text.AsSpan(next), out var after, out _) == OperationStatus.Done
				&& Rune.IsLower(after);
			if ((upperAfterLower || upperBeforeLower) && index > partStart) {
				yield return Part.Create(text, partStart, index);
				partStart = index;
			}

			if (!SpellWord.IsCombiningMark(current)) {
				previous = current;
			}
			index = next;
		}

		if (partStart < end) {
			yield return Part.Create(text, partStart, end);
		}
	}

	private static bool IsIgnored(ReadOnlySpan<char> candidate) {
		if (candidate.IndexOf('@') >= 0 || candidate.Contains("://", StringComparison.Ordinal)
			|| candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
			|| candidate.IndexOf('/') >= 0 || candidate.IndexOf('\\') >= 0 || candidate.StartsWith('#')) {
			return true;
		}

		if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		return candidate.Length >= 7 && candidate.ToString().All(IsHexCharacter);
	}

	private static bool IsHexCharacter(char value) => value is >= '0' and <= '9'
		or >= 'a' and <= 'f' or >= 'A' and <= 'F';

	private static bool HasMinimumLetters(string word) {
		int count = 0;
		foreach (var rune in word.EnumerateRunes()) {
			if (Rune.IsLetter(rune) && ++count >= MinWordLength) {
				return true;
			}
		}

		return false;
	}

	private static bool IsAllUpper(string word) {
		bool hasLetter = false;
		foreach (var rune in word.EnumerateRunes()) {
			if (Rune.IsLower(rune)) {
				return false;
			}

			hasLetter |= Rune.IsLetter(rune);
		}

		return hasLetter;
	}

	private static bool TryCandidateRune(string text, int index, out Rune rune, out int width) {
		Rune.DecodeFromUtf16(text.AsSpan(index), out rune, out width);
		return Rune.IsLetterOrDigit(rune) || SpellWord.IsCombiningMark(rune)
			|| rune.Value is '_' or '-' or '\'' or 0x2019 or '.' or '/' or '\\' or ':' or '@' or '#';
	}

	private static bool TryWordRune(string text, int index, out Rune rune, out int width) {
		Rune.DecodeFromUtf16(text.AsSpan(index), out rune, out width);
		return Rune.IsLetter(rune) || SpellWord.IsCombiningMark(rune) || rune.Value is '\'' or 0x2019;
	}

	private static int RuneWidth(string text, int index) => char.IsHighSurrogate(text[index]) && index + 1 < text.Length
		&& char.IsLowSurrogate(text[index + 1]) ? 2 : 1;

	private readonly record struct Part(int Start, int Length, string Word) {
		internal static Part Create(string text, int start, int end) {
			int wordEnd = IsPluralPossessive(text, start, end) ? end - 1 : end;
			return new Part(start, wordEnd - start, text[start..wordEnd]);
		}

		private static bool IsPluralPossessive(string text, int start, int end) =>
			end - start >= 2
			&& text[end - 1] is '\'' or '\u2019'
			&& text[end - 2] is 's' or 'S';
	}
}
