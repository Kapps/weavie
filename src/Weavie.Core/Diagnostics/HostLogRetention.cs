namespace Weavie.Core.Diagnostics;

/// <summary>
/// Bounds the logs directory against <see cref="WeaviePaths.HostLogFile"/>: each host launch mints a new
/// timestamped file there that nothing else ever removes, so left alone the directory grows by one file per
/// launch for the life of the install.
/// </summary>
public static class HostLogRetention {
	private const int MaxRetained = 20;

	/// <summary>
	/// Deletes all but the <see cref="MaxRetained"/> most-recently-written host logs in <paramref name="directory"/>.
	/// Call once per launch, before minting this run's own file. Best-effort: a failure here must not block startup.
	/// </summary>
	public static void Prune(string directory) {
		if (!Directory.Exists(directory)) return;
		try {
			foreach (var file in new DirectoryInfo(directory)
				.EnumerateFiles($"{WeaviePaths.HostLogPrefix}*.log")
				.OrderByDescending(f => f.LastWriteTimeUtc)
				.Skip(MaxRetained)) {
				file.Delete();
			}
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Retention is hygiene, not correctness: a locked or permission-denied file just waits for next launch.
		}
	}
}
