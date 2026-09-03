using System.Diagnostics;
using System.Text;

namespace Weavie.Linux.Hosting;

internal static class LinuxDesktopIdentity {
	internal const string AppId = "io.github.kapps.weavie";
	private const string DesktopFile = AppId + ".desktop";
	private const string BundledIconFile = "weavie.png";
	private const string InstalledIconFile = AppId + ".png";

	internal static void EnsureInstalled() {
		string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
		string dataHome = string.IsNullOrWhiteSpace(xdgDataHome)
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
			: xdgDataHome;
		string executable = Environment.ProcessPath
			?? throw new InvalidOperationException("The running Weavie executable path is unavailable.");
		EnsureInstalled(AppContext.BaseDirectory, dataHome, executable);
	}

	internal static void EnsureInstalled(string appDirectory, string dataHome, string executable) {
		string execValue = QuoteExec(executable);
		string applications = Path.Combine(dataHome, "applications");
		if (!MayClaim(Path.Combine(applications, DesktopFile), executable)) {
			return;
		}

		string desktopSource = Path.Combine(appDirectory, DesktopFile);
		string iconSource = Path.Combine(appDirectory, BundledIconFile);
		RequireBundledAsset(desktopSource, "desktop entry");
		RequireBundledAsset(iconSource, "app icon");

		string iconRoot = Path.Combine(dataHome, "icons");
		string iconDestination = Path.Combine(
			iconRoot, "hicolor", "512x512", "apps", InstalledIconFile);
		if (CopyIfChanged(iconSource, iconDestination)) {
			Directory.SetLastWriteTimeUtc(iconRoot, DateTime.UtcNow);
		}

		string[] desktopEntry = File.ReadAllLines(desktopSource);
		int execLine = Array.FindIndex(desktopEntry, line => line.StartsWith("Exec=", StringComparison.Ordinal));
		if (execLine < 0) {
			throw new InvalidDataException("The Linux desktop entry shipped with Weavie has no Exec field.");
		}
		// %U appends the paths the file manager passed; the field code sits outside the quoted executable,
		// whose own % is escaped to %% by QuoteExec.
		desktopEntry[execLine] = $"Exec={execValue} %U";
		if (WriteIfChanged(string.Join('\n', desktopEntry) + '\n', Path.Combine(applications, DesktopFile))) {
			RefreshMimeCache(applications);
		}
	}

	// GIO indexes application directories itself, but desktops reading mimeinfo.cache (KDE) only see a new
	// MimeType= after the cache is rebuilt. The tool ships with the desktop, not with Weavie, so its absence is
	// the environment's answer rather than a failure to report.
	private static void RefreshMimeCache(string applicationsDirectory) {
		try {
			using var refresh = Process.Start(new ProcessStartInfo("update-desktop-database", [applicationsDirectory]) {
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			});
			refresh?.WaitForExit(5_000);
		} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
			// No update-desktop-database on this system; GIO-based desktops index the directory without it.
		}
	}

	/// <summary>
	/// Whether this build may own the user's desktop entry. An entry naming a different Weavie that still
	/// exists belongs to that installation — a build run out of a source tree would otherwise take the file
	/// association with it and leave the installed app unreachable from the desktop once the tree is gone.
	/// </summary>
	private static bool MayClaim(string installedEntry, string executable) {
		if (!File.Exists(installedEntry)) {
			return true;
		}

		string? claimed = File.ReadLines(installedEntry)
			.FirstOrDefault(line => line.StartsWith("Exec=", StringComparison.Ordinal));
		if (claimed is null) {
			return true;
		}

		string path = UnquoteExec(claimed["Exec=".Length..]);
		return path.Length == 0
			|| !File.Exists(path)
			|| string.Equals(path, Path.GetFullPath(executable), StringComparison.Ordinal);
	}

	// The inverse of QuoteExec for the one field we wrote: the quoted path, minus the trailing field code.
	private static string UnquoteExec(string execValue) {
		string value = execValue.Trim();
		if (!value.StartsWith('"')) {
			return value;
		}

		int closing = value.LastIndexOf('"');
		return closing <= 0
			? value
			: value[1..closing]
				.Replace("%%", "%", StringComparison.Ordinal)
				.Replace("\\\\", "\\", StringComparison.Ordinal)
				.Replace("\\\"", "\"", StringComparison.Ordinal)
				.Replace("\\`", "`", StringComparison.Ordinal)
				.Replace("\\$", "$", StringComparison.Ordinal);
	}

	private static void RequireBundledAsset(string path, string description) {
		if (!File.Exists(path)) {
			throw new FileNotFoundException($"The Linux {description} shipped with Weavie is missing.", path);
		}
	}

	private static bool CopyIfChanged(string source, string destination) {
		if (File.Exists(destination)
			&& File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(destination))) {
			return false;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		File.Copy(source, destination, overwrite: true);
		return true;
	}

	private static bool WriteIfChanged(string content, string destination) {
		if (File.Exists(destination)
			&& string.Equals(File.ReadAllText(destination), content, StringComparison.Ordinal)) {
			return false;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		File.WriteAllText(destination, content);
		return true;
	}

	private static string QuoteExec(string executable) {
		ArgumentException.ThrowIfNullOrWhiteSpace(executable);
		if (!Path.IsPathFullyQualified(executable)) {
			throw new InvalidDataException("The running Weavie executable path is not absolute.");
		}
		if (executable.Contains('=') || executable.Any(char.IsControl)) {
			throw new InvalidDataException("The running Weavie executable path cannot be represented by a desktop entry.");
		}

		var quoted = new StringBuilder(executable.Length + 2).Append('"');
		foreach (char character in executable) {
			switch (character) {
				case '\\':
					quoted.Append('\\', 4);
					break;
				case '"':
				case '`':
				case '$':
					quoted.Append('\\', 2).Append(character);
					break;
				case '%':
					quoted.Append("%%");
					break;
				default:
					quoted.Append(character);
					break;
			}
		}
		return quoted.Append('"').ToString();
	}
}
