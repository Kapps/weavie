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
		desktopEntry[execLine] = $"Exec={execValue}";
		WriteIfChanged(
			string.Join('\n', desktopEntry) + '\n',
			Path.Combine(dataHome, "applications", DesktopFile));
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

	private static void WriteIfChanged(string content, string destination) {
		if (File.Exists(destination)
			&& string.Equals(File.ReadAllText(destination), content, StringComparison.Ordinal)) {
			return;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		File.WriteAllText(destination, content);
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
