using System.Runtime.InteropServices;

namespace Weavie.Hosting;

/// <summary>
/// Records how each run ended, so a run that simply vanishes leaves something behind. <see cref="CrashReporter"/>
/// only sees a managed exception; a process stopped from outside — killed under memory pressure, sent a signal,
/// terminated by the window server — writes no report of its own and, on macOS, no <c>.ips</c> either, so the app
/// disappears with nothing to read afterwards. This marks the run live at startup and stamps how it ended on the
/// way out; a marker still reading "running" on the next launch is the evidence that it was stopped rather than
/// quit. Callers pass their own file path (an install's <c>~/.weavie/logs</c>, or a test's temp dir) so unrelated
/// hosts never race one global location.
/// </summary>
public static class ExitJournal {
	private const string LiveMarker = "running:";
	// SIGKILL is deliberately absent: it cannot be observed, and the live marker surviving is exactly how that
	// ending (macOS's memory-pressure kill among them) is recognized.
	private static readonly PosixSignal[] ObservableSignals =
		[PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP];
	// A registration only observes its signal while it is reachable: its finalizer unregisters it, so dropping
	// these would silently stop recording signals at the first GC — the very endings the journal exists to name.
	private static readonly List<PosixSignalRegistration> Registrations = [];
	private static int _installed;
	private static readonly Lock EndingGate = new();
	private static bool _signalRecorded;
	private static string _path = string.Empty;

	/// <summary>
	/// Marks this run live and returns how the previous run ended, or <see langword="null"/> when it ended
	/// cleanly or left no journal. Idempotent; the first call wins, so a multi-window host reads one history.
	/// </summary>
	/// <param name="log">Sink for a one-line note when the previous run's ending is recovered.</param>
	/// <param name="journalPath">Where the marker lives.</param>
	public static string? Start(Action<string> log, string journalPath) {
		ArgumentNullException.ThrowIfNull(log);
		ArgumentException.ThrowIfNullOrEmpty(journalPath);
		if (Interlocked.Exchange(ref _installed, 1) != 0) {
			return null;
		}

		_path = journalPath;

		string? unfinished = ReadUnfinishedRun(journalPath);
		if (unfinished is not null) {
			log($"previous run ended without shutting down: {unfinished}");
		}

		if (!MarkRunning(journalPath)) {
			log($"could not mark this run live at {journalPath}; its ending will not be explained");
		}

		AppDomain.CurrentDomain.ProcessExit += (_, _) => Record("exited");
		foreach (var signal in ObservableSignals) {
			// Observed, never handled: the runtime's own disposition still applies, this only leaves the reason.
			Registrations.Add(PosixSignalRegistration.Create(signal, context => {
				lock (EndingGate) {
					Record($"signalled {context.Signal}");
					_signalRecorded = true;
				}
			}));
		}

		return unfinished;
	}

	/// <summary>
	/// Stamps how this run is ending, replacing the live marker. A host whose shutdown its runtime cannot see —
	/// AppKit terminating the app — records it here so the ending reads as deliberate rather than as a kill.
	/// </summary>
	/// <param name="reason">What ended the run, in the words the next launch should show.</param>
	public static void Record(string reason) {
		ArgumentException.ThrowIfNullOrEmpty(reason);
		lock (EndingGate) {
			if (!_signalRecorded && Volatile.Read(ref _path) is { Length: > 0 } path) {
				MarkEnded(path, reason);
			}
		}
	}

	/// <summary>The signal registrations this journal is holding open, so a test can pin that it still holds them.</summary>
	internal static IReadOnlyList<PosixSignalRegistration> HeldRegistrations => Registrations;

	/// <summary>The previous run's signal or unfinished live marker, else null.</summary>
	internal static string? ReadUnfinishedRun(string journalPath) {
		try {
			if (!File.Exists(journalPath)
				|| File.ReadAllText(journalPath).Trim() is not { Length: > 0 } previous) {
				return null;
			}

			if (previous.StartsWith("signalled ", StringComparison.Ordinal)) {
				return previous;
			}
			if (!previous.StartsWith(LiveMarker, StringComparison.Ordinal)) {
				return null;
			}

			// A marker whose process is still running is that process's, not a previous run's: two hosts can share
			// a machine (a worker handing over to its replacement, a headless host beside the desktop app).
			return StillRunning(previous) ? null : previous;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	private static bool StillRunning(string marker) {
		int start = marker.IndexOf("pid ", StringComparison.Ordinal);
		int end = marker.IndexOf(',', start < 0 ? 0 : start);
		if (start < 0 || end < 0
			|| !int.TryParse(marker.AsSpan(start + 4, end - start - 4), out int pid)
			|| pid == Environment.ProcessId) {
			return false;
		}

		try {
			using var owner = System.Diagnostics.Process.GetProcessById(pid);
			return !owner.HasExited;
		} catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
			return false;
		}
	}

	internal static bool MarkRunning(string journalPath) =>
		Write(journalPath, $"{LiveMarker} pid {Environment.ProcessId}, since {DateTimeOffset.Now:o}");

	internal static void MarkEnded(string journalPath, string reason) =>
		Write(journalPath, $"{reason}: {DateTimeOffset.Now:o}");

	private static bool Write(string journalPath, string entry) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
			File.WriteAllText(journalPath, entry);
			return true;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// A dying process can't do anything about a failed marker write, and losing it only costs the next
			// launch its explanation — never the shutdown itself.
			return false;
		}
	}
}
