namespace Weavie.Core.Spelling;

/// <summary>The embedded Hunspell locales supported by Weavie.</summary>
public static class SpellLocales {
	/// <summary>American English.</summary>
	public const string EnUs = "en-US";

	/// <summary>British English.</summary>
	public const string EnGb = "en-GB";

	/// <summary>Canadian English.</summary>
	public const string EnCa = "en-CA";

	/// <summary>Australian English.</summary>
	public const string EnAu = "en-AU";

	/// <summary>Every locale that is bundled with Weavie.</summary>
	public static IReadOnlyList<string> Supported { get; } = [EnUs, EnGb, EnCa, EnAu];

	/// <summary>Returns whether <paramref name="locale"/> names one of the embedded dictionaries.</summary>
	public static bool IsSupported(string locale) => Supported.Contains(locale, StringComparer.Ordinal);

	internal static string ResourceStem(string locale) {
		if (!IsSupported(locale)) {
			throw new ArgumentException($"Unsupported spell-check locale '{locale}'.", nameof(locale));
		}

		return locale.Replace('-', '_');
	}
}
