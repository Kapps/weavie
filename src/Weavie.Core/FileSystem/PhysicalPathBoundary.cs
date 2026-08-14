namespace Weavie.Core.FileSystem;

/// <summary>Resolves links before confining an untrusted operating-system path to one physical directory.</summary>
public static class PhysicalPathBoundary {
	/// <summary>
	/// Returns the link-resolved path when it is physically inside <paramref name="root"/>. The optional missing
	/// leaf supports creating a new file while requiring every parent directory to already exist and remain confined.
	/// </summary>
	public static string ResolveWithin(string root, string path, bool allowMissingLeaf) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentException.ThrowIfNullOrEmpty(path);
		string fullRoot = Path.GetFullPath(root);
		string fullPath = Path.GetFullPath(path);
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		if (!PathBoundary.Contains(fullRoot, fullPath, comparison)) {
			throw new UnauthorizedAccessException($"Path is outside the allowed root: {path}");
		}

		string resolvedRoot = Resolve(fullRoot, allowMissingLeaf: false);
		string resolvedPath = Resolve(fullPath, allowMissingLeaf);
		if (!PathBoundary.Contains(resolvedRoot, resolvedPath, comparison)) {
			throw new UnauthorizedAccessException($"Path resolves outside the allowed root: {path}");
		}
		return resolvedPath;
	}

	private static string Resolve(string path, bool allowMissingLeaf) {
		string root = Path.GetPathRoot(path)
			?? throw new IOException($"Path has no filesystem root: {path}");
		string[] segments = path[root.Length..].Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries);
		string current = root;
		for (int index = 0; index < segments.Length; index++) {
			string candidate = Path.Combine(current, segments[index]);
			if (TryResolveLink(candidate, out string? target)) {
				current = target;
				continue;
			}
			if (File.Exists(candidate) || Directory.Exists(candidate)) {
				current = candidate;
				continue;
			}
			if (allowMissingLeaf && index == segments.Length - 1) {
				return candidate;
			}
			throw new FileNotFoundException($"Path component does not exist: {candidate}", candidate);
		}
		return Path.GetFullPath(current);
	}

	private static bool TryResolveLink(string path, out string target) {
		FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
		try {
			if (info.LinkTarget is null) {
				target = string.Empty;
				return false;
			}
			var resolved = info.ResolveLinkTarget(returnFinalTarget: true)
				?? throw new IOException($"Filesystem link could not be resolved: {path}");
			target = Path.GetFullPath(resolved.FullName);
			return true;
		} catch (FileNotFoundException ex) {
			throw new IOException($"Filesystem link is dangling: {path}", ex);
		}
	}
}
