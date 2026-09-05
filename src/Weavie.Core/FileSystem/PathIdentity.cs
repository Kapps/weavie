namespace Weavie.Core.FileSystem;

/// <summary>
/// The one answer to "are these the same path". A path is canonicalized with
/// <see cref="Path.GetFullPath(string)"/> and stripped of its trailing separator, then compared under the
/// platform's own case rule — case-insensitive only on Windows, whose filesystem API is. Normalization happens
/// inside every member, <see cref="Comparer"/> included, so a dictionary or set keyed by a path agrees with
/// itself by construction rather than by each call site remembering to normalize first.
/// <para>
/// Identity is decided from the path string: links are not followed and the real on-disk casing is not read, so
/// two spellings on a case-insensitive macOS volume still compare as distinct. Use <see cref="PhysicalPath"/>
/// when identity must be resolved against the filesystem itself.
/// </para>
/// </summary>
public static class PathIdentity {
	/// <summary>The platform's path case rule: case-insensitive on Windows, case-sensitive everywhere else.</summary>
	public static StringComparison Comparison =>
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	/// <summary>Comparer for any dictionary or set keyed by a filesystem path.</summary>
	public static StringComparer Comparer { get; } = new NormalizingPathComparer();

	/// <summary>
	/// Comparer for a hot path whose every value is already known canonical — produced by
	/// <see cref="Normalize(string)"/> or an equivalent producer such as <c>WorkspacePaths.CanonicalFsPath</c>. Applies
	/// only the platform case rule, skipping the renormalization <see cref="Comparer"/> repeats on every
	/// comparison. Using this on a path that may not already be canonical reintroduces the case-rule bugs
	/// <see cref="Comparer"/> exists to prevent — reach for <see cref="Comparer"/> unless the values are
	/// provably canonical already.
	/// </summary>
	public static StringComparer CanonicalComparer { get; } = StringComparer.FromComparison(Comparison);

	/// <summary>The canonical absolute form of <paramref name="path"/>, without a trailing separator.</summary>
	public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

	/// <summary>The canonical form of <paramref name="path"/> resolved against <paramref name="basePath"/>.</summary>
	public static string Normalize(string path, string basePath) =>
		Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, basePath));

	/// <summary>Whether <paramref name="left"/> and <paramref name="right"/> name the same path.</summary>
	public static bool Equals(string left, string right) =>
		string.Equals(Normalize(left), Normalize(right), Comparison);

	private sealed class NormalizingPathComparer : StringComparer {
		public override int Compare(string? x, string? y) => string.Compare(Key(x), Key(y), Comparison);

		public override bool Equals(string? x, string? y) => string.Equals(Key(x), Key(y), Comparison);

		public override int GetHashCode(string obj) => Normalize(obj).GetHashCode(Comparison);

		private static string? Key(string? path) => path is null ? null : Normalize(path);
	}
}
