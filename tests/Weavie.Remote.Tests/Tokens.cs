namespace Weavie.Remote.Tests;

/// <summary>
/// The token variants every auth surface must reject — missing, empty, wrong, wrong-length (short/long), and
/// case-flipped — versus the one correct token. Centralized so the headless and runner suites probe the exact
/// same matrix.
/// </summary>
internal static class Tokens {
	/// <summary>The host's configured token (32 lowercase hex chars), the only value that authorizes.</summary>
	public const string Correct = "0123456789abcdef0123456789abcdef";

	/// <summary>The variants that MUST be denied (xUnit MemberData).</summary>
	public static IEnumerable<object[]> Denied() {
		yield return ["missing"];
		yield return ["empty"];
		yield return ["wrong"];
		yield return ["short"];
		yield return ["long"];
		yield return ["uppercase"];
	}

	/// <summary>The raw token a variant presents, or <c>null</c> when it presents none at all.</summary>
	public static string? Value(string variant) => variant switch {
		"missing" => null,
		"empty" => string.Empty,
		"wrong" => "ffffffffffffffffffffffffffffffff",
		"short" => Correct[..16],
		"long" => Correct + "00",
		"uppercase" => Correct.ToUpperInvariant(),
		"correct" => Correct,
		_ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "unknown token variant"),
	};

	/// <summary>The <c>?token=</c> query suffix for a variant ("" when it presents none).</summary>
	public static string QuerySuffix(string variant) {
		string? token = Value(variant);
		return token is null ? string.Empty : "?token=" + Uri.EscapeDataString(token);
	}
}
