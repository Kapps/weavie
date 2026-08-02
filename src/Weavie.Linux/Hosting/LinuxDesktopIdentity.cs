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
		EnsureInstalled(AppContext.BaseDirectory, dataHome);
	}

	internal static void EnsureInstalled(string appDirectory, string dataHome) {
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

		CopyIfChanged(desktopSource, Path.Combine(dataHome, "applications", DesktopFile));
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
}
