using Weavie.Core;

namespace Weavie.Hosting;

/// <summary>
/// Process-wide last-resort crash visibility. Without it, an unhandled exception on a background thread (a PTY,
/// supervisor, or MCP worker) tears the app down with nothing written anywhere — the user just sees a silent hard
/// exit. <see cref="Install"/> records each terminating exception to <see cref="WeaviePaths.LastCrashFile"/> and
/// stderr before the runtime exits; <see cref="TakePendingReport"/> hands the next launch that report so it can
/// surface "Weavie exited unexpectedly" instead of pretending nothing happened.
/// </summary>
public static class CrashReporter {
	private static int _installed;

	/// <summary>
	/// Installs the process unhandled-exception + unobserved-task handlers (idempotent; the first call wins). A
	/// terminating exception is appended to the crash log; an unobserved task exception only reaches stderr/log,
	/// since it doesn't bring the process down.
	/// </summary>
	/// <param name="log">Sink for a one-line note when a crash is recorded.</param>
	public static void Install(Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		if (Interlocked.Exchange(ref _installed, 1) != 0) {
			return;
		}

		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			Record(log, "unhandled exception", e.ExceptionObject as Exception, fatal: true);
		TaskScheduler.UnobservedTaskException += (_, e) => {
			Record(log, "unobserved task exception", e.Exception, fatal: false);
			e.SetObserved();
		};
	}

	/// <summary>
	/// Returns the prior run's crash report and rotates it to <see cref="WeaviePaths.PreviousCrashFile"/> so it
	/// surfaces exactly once, or <c>null</c> when the last run exited cleanly. Call on startup to drive a one-time
	/// "exited unexpectedly" notice; the rotated file keeps the detail for inspection.
	/// </summary>
	public static string? TakePendingReport() {
		try {
			if (!File.Exists(WeaviePaths.LastCrashFile)) {
				return null;
			}

			string report = File.ReadAllText(WeaviePaths.LastCrashFile);
			File.Move(WeaviePaths.LastCrashFile, WeaviePaths.PreviousCrashFile, overwrite: true);
			return string.IsNullOrWhiteSpace(report) ? null : report;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	private static void Record(Action<string> log, string kind, Exception? exception, bool fatal) {
		Console.Error.WriteLine($"[weavie] {kind}: {exception}");
		Console.Error.Flush();
		if (!fatal) {
			return;
		}

		try {
			Directory.CreateDirectory(WeaviePaths.Logs);
			File.AppendAllText(
				WeaviePaths.LastCrashFile,
				$"{DateTimeOffset.Now:o} {kind}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
			log($"recorded {kind} to {WeaviePaths.LastCrashFile}");
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// A dying process can't do anything about a failed log write; the stderr line above still carries it.
		}
	}
}
