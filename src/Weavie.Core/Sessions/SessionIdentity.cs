namespace Weavie.Core.Sessions;

/// <summary>
/// Derives a session's visual identity (hue + monogram) deterministically from its label (branch name), so
/// the same branch always gets the same chip on the rail.
/// </summary>
public static class SessionIdentity {
	/// <summary>
	/// A stable hue (0–359) for <paramref name="label"/> via FNV-1a — not <see cref="object.GetHashCode"/>,
	/// which is randomized per process.
	/// </summary>
	public static int Hue(string label) {
		ArgumentNullException.ThrowIfNull(label);
		uint hash = 2166136261u;
		foreach (char c in label) {
			hash = (hash ^ c) * 16777619u;
		}

		return (int)(hash % 360u);
	}

	/// <summary>
	/// A 1–2 character uppercase monogram for <paramref name="label"/>: the branch leaf's first two words'
	/// initials (<c>feature/auth-refactor</c> → <c>AR</c>), else the leaf's first two characters.
	/// </summary>
	public static string Monogram(string label) {
		ArgumentException.ThrowIfNullOrEmpty(label);
		int slash = label.LastIndexOf('/');
		string leaf = slash >= 0 && slash + 1 < label.Length ? label[(slash + 1)..] : label;
		string[] words = leaf.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
		string letters = words.Length >= 2
			? $"{words[0][0]}{words[1][0]}"
			: leaf.Length >= 2 ? leaf[..2] : leaf;
		letters = new string([.. letters.Where(char.IsLetterOrDigit)]);
		return letters.Length == 0 ? "?" : letters.ToUpperInvariant();
	}
}
