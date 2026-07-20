namespace Weavie.Core.Workspaces;

/// <summary>
/// Recovers a loosely-written file reference — a repo-relative path missing its leading folder(s), or a bare
/// filename — by matching it as a whole-segment suffix against the workspace file index. Powers the smart
/// reveal for clicked links whose path doesn't resolve directly.
/// </summary>
public static class PathSuffixMatcher {
	/// <summary>
	/// Canonicalizes a loose reference for matching (and for preloading Go-to-File): separators become
	/// <c>/</c> and leading <c>./</c>, <c>../</c>, and <c>/</c> segments are dropped. Empty when nothing
	/// matchable remains.
	/// </summary>
	public static string Normalize(string reference) {
		ArgumentNullException.ThrowIfNull(reference);
		string term = reference.Replace('\\', '/');
		int start = 0;
		while (start < term.Length) {
			if (term[start] == '/') {
				start += 1;
			} else if (term.AsSpan(start).StartsWith("./", StringComparison.Ordinal)) {
				start += 2;
			} else if (term.AsSpan(start).StartsWith("../", StringComparison.Ordinal)) {
				start += 3;
			} else {
				break;
			}
		}

		return term[start..];
	}

	/// <summary>
	/// The files whose path ends with <paramref name="reference"/> on whole segments (never inside a name:
	/// <c>foo.ts</c> matches <c>src/foo.ts</c>, not <c>src/barfoo.ts</c>), comparing ordinal-ignore-case
	/// across either separator. The reference is normalized via <see cref="Normalize"/>; an empty result for
	/// an empty term.
	/// </summary>
	public static IReadOnlyList<string> Match(IReadOnlyList<string> files, string reference) {
		ArgumentNullException.ThrowIfNull(files);
		string term = Normalize(reference);
		if (term.Length == 0) {
			return [];
		}

		string suffix = "/" + term;
		var matches = new List<string>();
		foreach (string file in files) {
			if (file.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
				matches.Add(file);
			}
		}

		return matches;
	}
}
