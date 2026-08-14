namespace Weavie.Hosting;

/// <summary>
/// Process-wide last-resort crash visibility. Without it, an unhandled exception on a background thread (a PTY,
/// supervisor, or MCP worker) tears the app down with nothing written anywhere — the user just sees a silent hard
/// exit. <see cref="Install"/> records each terminating exception to the caller-supplied crash file and stderr
/// before the runtime exits; <see cref="TakePendingReport"/> hands the next launch that report so it can surface
/// "Weavie exited unexpectedly" instead of pretending nothing happened. Callers pass their own file paths (an app
/// install's real <c>~/.weavie/logs</c>, or a test's private temp dir) rather than this type owning one global
/// location, so unrelated concurrent hosts in the same process never race the same file.
/// </summary>
public static class CrashReporter {
	private static int _installed;

	/// <summary>
	/// Installs the process unhandled-exception + unobserved-task handlers (idempotent; the first call wins). A
	/// terminating exception is appended to <paramref name="lastCrashFile"/>; an unobserved task exception only
	/// reaches stderr/log, since it doesn't bring the process down.
	/// </summary>
	/// <param name="log">Sink for a one-line note when a crash is recorded.</param>
	/// <param name="lastCrashFile">Where a terminating exception is appended.</param>
	public static void Install(Action<string> log, string lastCrashFile) {
		ArgumentNullException.ThrowIfNull(log);
		ArgumentException.ThrowIfNullOrEmpty(lastCrashFile);
		if (Interlocked.Exchange(ref _installed, 1) != 0) {
			return;
		}

		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			Record(log, lastCrashFile, "unhandled exception", e.ExceptionObject as Exception, fatal: true);
		TaskScheduler.UnobservedTaskException += (_, e) => {
			Record(log, lastCrashFile, "unobserved task exception", e.Exception, fatal: false);
			e.SetObserved();
		};
	}

	/// <summary>
	/// Returns the prior run's crash report and rotates <paramref name="lastCrashFile"/> to
	/// <paramref name="previousCrashFile"/> so it surfaces exactly once, or <c>null</c> when the last run exited
	/// cleanly. Call on startup to drive a one-time "exited unexpectedly" notice; the rotated file keeps the detail
	/// for inspection.
	/// </summary>
	public static string? TakePendingReport(string lastCrashFile, string previousCrashFile) {
		ArgumentException.ThrowIfNullOrEmpty(lastCrashFile);
		ArgumentException.ThrowIfNullOrEmpty(previousCrashFile);
		try {
			if (!File.Exists(lastCrashFile)) {
				return null;
			}

			string report = File.ReadAllText(lastCrashFile);
			File.Move(lastCrashFile, previousCrashFile, overwrite: true);
			return string.IsNullOrWhiteSpace(report) ? null : report;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	private static void Record(Action<string> log, string lastCrashFile, string kind, Exception? exception, bool fatal) {
		Console.Error.WriteLine($"[weavie] {kind}: {exception}");
		Console.Error.Flush();
		if (!fatal) {
			return;
		}

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(lastCrashFile)!);
			File.AppendAllText(
				lastCrashFile,
				$"{DateTimeOffset.Now:o} {kind}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
			log($"recorded {kind} to {lastCrashFile}");
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// A dying process can't do anything about a failed log write; the stderr line above still carries it.
		}
	}
}
