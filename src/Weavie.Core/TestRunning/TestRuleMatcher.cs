using System.Text;
using System.Text.RegularExpressions;

namespace Weavie.Core.TestRunning;

/// <summary>
/// Selects the test rule for a file: the first rule in a <see cref="TestProfile"/> whose glob matches the
/// workspace-relative path. The glob supports <c>**</c>, <c>*</c>, <c>?</c>, <c>{a,b}</c> alternation, and
/// <c>?(x)</c> optional groups — the subset the profile needs — translated to an anchored regex. Mirrors the
/// web's <c>glob.ts</c> across the language boundary (test running resolves rules in both worlds).
/// </summary>
public static class TestRuleMatcher {
	/// <summary>Returns the first rule whose glob matches <paramref name="relativePath"/> (path separators normalized), or null.</summary>
	public static TestRule? Match(TestProfile profile, string relativePath) {
		ArgumentNullException.ThrowIfNull(profile);
		string normalized = relativePath.Replace('\\', '/');
		foreach (var rule in profile.Rules) {
			if (new Regex(GlobToRegex(rule.Glob)).IsMatch(normalized)) {
				return rule;
			}
		}

		return null;
	}

	/// <summary>Translates a glob into an anchored regex string.</summary>
	public static string GlobToRegex(string glob) => "^" + Translate(glob) + "$";

	private static string Translate(string glob) {
		var sb = new StringBuilder();
		int i = 0;
		while (i < glob.Length) {
			char c = glob[i];
			switch (c) {
				case '*':
					if (i + 1 < glob.Length && glob[i + 1] == '*') {
						if (i + 2 < glob.Length && glob[i + 2] == '/') {
							sb.Append("(?:.*/)?"); // **/ matches any number of leading directories, including none
							i += 3;
						} else {
							sb.Append(".*"); // ** crosses directory separators
							i += 2;
						}
					} else {
						sb.Append("[^/]*"); // * stays within a path segment
						i++;
					}

					break;
				case '?':
					if (i + 1 < glob.Length && glob[i + 1] == '(' && FindClose(glob, i + 1) is { } optClose) {
						sb.Append("(?:").Append(TranslateAlternation(glob[(i + 2)..optClose])).Append(")?");
						i = optClose + 1;
					} else {
						sb.Append("[^/]");
						i++;
					}

					break;
				case '{':
					if (FindClose(glob, i) is { } braceClose) {
						sb.Append("(?:").Append(TranslateAlternation(glob[(i + 1)..braceClose])).Append(')');
						i = braceClose + 1;
					} else {
						sb.Append(Regex.Escape("{"));
						i++;
					}

					break;
				case '/':
					sb.Append('/');
					i++;
					break;
				default:
					sb.Append(Regex.Escape(c.ToString()));
					i++;
					break;
			}
		}

		return sb.ToString();
	}

	// Translates comma-separated alternatives (each itself a glob) into a regex `a|b|c`.
	private static string TranslateAlternation(string inner) =>
		string.Join('|', SplitTopLevel(inner).Select(Translate));

	// Splits on commas not nested inside { } or ( ), so {a,b} alternatives survive nesting.
	private static IEnumerable<string> SplitTopLevel(string inner) {
		var parts = new List<string>();
		int depth = 0;
		int start = 0;
		for (int i = 0; i < inner.Length; i++) {
			char c = inner[i];
			if (c is '{' or '(') {
				depth++;
			} else if (c is '}' or ')') {
				depth--;
			} else if (c == ',' && depth == 0) {
				parts.Add(inner[start..i]);
				start = i + 1;
			}
		}

		parts.Add(inner[start..]);
		return parts;
	}

	// The index of the bracket closing the one at `open` ('{'→'}' or '('→')'), honoring nesting, or null.
	private static int? FindClose(string glob, int open) {
		char close = glob[open] == '{' ? '}' : ')';
		int depth = 0;
		for (int i = open; i < glob.Length; i++) {
			if (glob[i] == glob[open]) {
				depth++;
			} else if (glob[i] == close) {
				depth--;
				if (depth == 0) {
					return i;
				}
			}
		}

		return null;
	}
}
