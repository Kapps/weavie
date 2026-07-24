using System.Globalization;
using System.Text;

namespace Weavie.Core.Spelling;

internal static class SpellWord {
	internal static bool TryNormalize(string value, out string normalized) {
		if (string.IsNullOrWhiteSpace(value)) {
			normalized = string.Empty;
			return false;
		}

		normalized = value.Trim().Normalize(NormalizationForm.FormC).Replace('\u2019', '\'');
		var runes = normalized.EnumerateRunes().ToArray();
		if (runes.Length == 0) {
			return false;
		}

		for (int index = 0; index < runes.Length; index++) {
			var rune = runes[index];
			if (Rune.IsLetter(rune)) {
				continue;
			}

			if (IsCombiningMark(rune) && index > 0
				&& (Rune.IsLetter(runes[index - 1]) || IsCombiningMark(runes[index - 1]))) {
				continue;
			}

			if (rune.Value == '\'' && index > 0 && index < runes.Length - 1
				&& (Rune.IsLetter(runes[index - 1]) || IsCombiningMark(runes[index - 1]))
				&& Rune.IsLetter(runes[index + 1])) {
				continue;
			}

			normalized = string.Empty;
			return false;
		}

		return true;
	}

	internal static bool IsCombiningMark(Rune rune) => Rune.GetUnicodeCategory(rune) is
		UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

	internal static string RequireNormalized(string value, string paramName) {
		ArgumentException.ThrowIfNullOrEmpty(value, paramName);
		if (!TryNormalize(value, out string normalized)) {
			throw new ArgumentException("A dictionary word must contain letters, with apostrophes only between letters.", paramName);
		}

		return normalized;
	}
}
