namespace Weavie.Core.FileSystem;

/// <summary>Canonical filesystem paths with existing links and on-disk casing resolved component by component.</summary>
public static class PhysicalPath {
	/// <summary>The platform's conservative filesystem path comparison.</summary>
	public static StringComparison Comparison =>
		OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

	/// <summary>Normalizes <paramref name="path"/> and resolves every existing filesystem entry it traverses.</summary>
	public static string Resolve(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		string fullPath = Path.GetFullPath(path);
		string root = Path.GetPathRoot(fullPath)
			?? throw new InvalidOperationException($"Path has no filesystem root: {fullPath}");
		string current = root;
		foreach (string segment in Path.GetRelativePath(root, fullPath).Split(
			Path.DirectorySeparatorChar,
			StringSplitOptions.RemoveEmptyEntries)) {
			string candidate = CanonicalEntry(current, segment);
			var entry = Entry(candidate);
			current = entry?.LinkTarget is not null
				? entry.ResolveLinkTarget(returnFinalTarget: true)!.FullName
				: candidate;
		}
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
	}

	/// <summary>Whether two paths resolve to the same physical path under platform comparison semantics.</summary>
	public static bool Equal(string left, string right) =>
		string.Equals(Resolve(left), Resolve(right), Comparison);

	/// <summary>Whether <paramref name="path"/> is physically equal to or beneath <paramref name="root"/>.</summary>
	public static bool IsSameOrDescendant(string path, string root) {
		string candidate = Resolve(path);
		string ancestor = Resolve(root);
		if (string.Equals(candidate, ancestor, Comparison)) {
			return true;
		}
		string prefix = Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar;
		return candidate.StartsWith(prefix, Comparison);
	}

	private static string CanonicalEntry(string parent, string segment) {
		string candidate = Path.Combine(parent, segment);
		if (!OperatingSystem.IsMacOS() || Entry(candidate) is null || !Directory.Exists(parent)) {
			return candidate;
		}
		string?[] names = [.. Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName)];
		string? actual = names.FirstOrDefault(name => string.Equals(name, segment, StringComparison.Ordinal))
			?? names.FirstOrDefault(name => string.Equals(name, segment, StringComparison.OrdinalIgnoreCase));
		return actual is null ? candidate : Path.Combine(parent, actual);
	}

	private static FileSystemInfo? Entry(string path) {
		var directory = new DirectoryInfo(path);
		if (directory.Exists || directory.LinkTarget is not null) {
			return directory;
		}
		var file = new FileInfo(path);
		return file.Exists || file.LinkTarget is not null ? file : null;
	}
}
