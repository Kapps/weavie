namespace Weavie.Core.Configuration;

/// <summary>
/// Resolves an executable by name against <c>PATH</c>, cross-platform. Windows honors <c>PATHEXT</c> (so
/// <c>"pwsh"</c> resolves to <c>pwsh.exe</c>); Unix matches the name verbatim.
/// </summary>
public static class ExecutableFinder {
	/// <summary>
	/// Returns the full path to <paramref name="name"/> if it exists or is found on <c>PATH</c>, else
	/// <c>null</c>. A qualified name is checked directly (with Windows extension probing); a bare name searches
	/// every <c>PATH</c> entry.
	/// </summary>
	public static string? FindOnPath(string name) {
		ArgumentException.ThrowIfNullOrEmpty(name);

		if (Path.IsPathRooted(name)
			|| name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) {
			if (File.Exists(name)) {
				return Path.GetFullPath(name);
			}

			return OperatingSystem.IsWindows() ? ProbeWindowsExtensions(name) : null;
		}

		string? path = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(path)) {
			return null;
		}

		foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			string candidate;
			try {
				candidate = Path.Combine(dir, name);
			} catch (ArgumentException) {
				continue; // a malformed PATH entry; skip it
			}

			if (File.Exists(candidate)) {
				return candidate;
			}

			if (OperatingSystem.IsWindows() && ProbeWindowsExtensions(candidate) is { } resolved) {
				return resolved;
			}
		}

		return null;
	}

	/// <summary>Appends each <c>PATHEXT</c> extension to <paramref name="candidate"/> and returns the first that exists.</summary>
	private static string? ProbeWindowsExtensions(string candidate) {
		string pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
		foreach (string ext in pathext.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			string withExt = candidate + ext;
			if (File.Exists(withExt)) {
				return withExt;
			}
		}

		return null;
	}
}
