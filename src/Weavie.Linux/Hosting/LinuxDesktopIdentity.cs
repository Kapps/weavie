namespace Weavie.Linux.Hosting;

internal static class LinuxDesktopIdentity {
	internal const string AppId = "io.github.kapps.weavie";
	private const string DesktopFile = AppId + ".desktop";

	internal static void EnsureInstalled() {
		string source = Path.Combine(AppContext.BaseDirectory, DesktopFile);
		if (!File.Exists(source)) {
			throw new FileNotFoundException("The Linux desktop identity shipped with Weavie is missing.", source);
		}

		string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
		string dataHome = string.IsNullOrWhiteSpace(xdgDataHome)
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
			: xdgDataHome;
		string applications = Path.Combine(dataHome, "applications");
		Directory.CreateDirectory(applications);
		string destination = Path.Combine(applications, DesktopFile);
		if (!File.Exists(destination)
			|| !File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(destination))) {
			File.Copy(source, destination, overwrite: true);
		}
	}
}
