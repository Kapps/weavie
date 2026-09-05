using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="ExitJournal"/> is what distinguishes a run that quit from one that was stopped: only the second
/// leaves the live marker behind. Each test uses its own temp path, for the same reason
/// <see cref="CrashReporterTests"/> does — concurrent <c>TestHost</c>s touch the real one.
/// </summary>
public sealed class ExitJournalTests {
	private static string IsolatedPath() =>
		Path.Combine(
			Path.GetTempPath(),
			"weavie-exit-journal-tests-" + Guid.NewGuid().ToString("n"),
			"last-exit.log");

	[Fact]
	public void NoJournal_ReadsAsNothingToExplain() =>
		Assert.Null(ExitJournal.ReadUnfinishedRun(IsolatedPath()));

	[Fact]
	public void ARunThatStampedItsEnding_ReadsAsNothingToExplain() {
		string path = IsolatedPath();
		ExitJournal.MarkRunning(path);
		ExitJournal.MarkEnded(path, "terminated by AppKit");

		Assert.Null(ExitJournal.ReadUnfinishedRun(path));
	}

	[Fact]
	public void ASignalledRun_RemainsVisibleOnNextLaunch() {
		string path = IsolatedPath();
		ExitJournal.MarkRunning(path);
		ExitJournal.MarkEnded(path, "signalled SIGTERM");

		Assert.StartsWith("signalled SIGTERM:", ExitJournal.ReadUnfinishedRun(path), StringComparison.Ordinal);
	}

	[Fact]
	public void ARunThatNeverEnded_ReadsBackAsUnfinished() {
		string path = IsolatedPath();
		ExitJournal.MarkRunning(path);

		// Nothing stamped an ending, which is all a kill leaves behind — no exception, no report, no signal.
		string? unfinished = ExitJournal.ReadUnfinishedRun(path);

		Assert.NotNull(unfinished);
		Assert.Contains($"pid {Environment.ProcessId}", unfinished, StringComparison.Ordinal);
	}

	[Fact]
	public void AMarkerFromALiveProcess_IsNotAPreviousRun() {
		string path = IsolatedPath();
		// A worker handing over to its replacement, or a headless host beside the desktop app: the owner is still
		// running, so its marker says nothing about how anything ended.
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, $"running: pid {LiveForeignProcessId()}, since {DateTimeOffset.Now:o}");

		Assert.Null(ExitJournal.ReadUnfinishedRun(path));
	}

	[Fact]
	public void AnUnwritableJournal_IsNotAFailureOfTheShutdown() {
		string unwritable = Path.Combine(Path.GetDirectoryName(IsolatedPath())!, "nested", "x.log");
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(unwritable)!)!);
		File.WriteAllText(Path.GetDirectoryName(unwritable)!, "not a directory");

		Assert.False(ExitJournal.MarkRunning(unwritable));
		Assert.Null(ExitJournal.ReadUnfinishedRun(unwritable));
	}

	[Fact]
	public void AStartedJournal_KeepsItsSignalRegistrationsReachable() {
		ExitJournal.Start(_ => { }, IsolatedPath());

		// A PosixSignalRegistration unregisters its handler when finalized, so dropping the return value would
		// silently end the signal leg at the first collection — every signal would then read as an outside kill.
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		Assert.NotEmpty(ExitJournal.HeldRegistrations);
	}

	// Any live pid that isn't this process: the read only asks whether the marker's owner is still running.
	private static int LiveForeignProcessId() {
		using var other = System.Diagnostics.Process.GetProcesses().First(p => p.Id != Environment.ProcessId);
		return other.Id;
	}
}
