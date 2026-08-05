namespace Weavie.Hosting.Inference;

/// <summary>A private scratch directory for one ephemeral inference CLI process.</summary>
internal sealed class InferenceTempDirectory : IDisposable {
	private InferenceTempDirectory(string path) {
		Path = path;
	}

	public string Path { get; }

	public static InferenceTempDirectory Create() {
		string path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			"weavie-inference-" + Guid.NewGuid().ToString("n"));
		if (OperatingSystem.IsWindows()) {
			Directory.CreateDirectory(path);
		} else {
			Directory.CreateDirectory(
				path,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		return new InferenceTempDirectory(path);
	}

	public void Dispose() => Directory.Delete(Path, recursive: true);
}
